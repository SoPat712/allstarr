import os
import sys
import asyncio
import subprocess
import shutil
import json
import logging
from pathlib import Path
from typing import Optional, List, Dict, Any
from fastapi import FastAPI, File, UploadFile, Query, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("gamdl-aio")

PROJECT_ROOT = Path(__file__).parent.resolve()
sys.path.insert(0, str(PROJECT_ROOT / "gamdl"))

try:
    from gamdl.api import AppleMusicApi
    from gamdl.api.wrapper import WrapperApi
    from gamdl.interface import AppleMusicBaseInterface, AppleMusicInterface, AppleMusicSongInterface
    from gamdl.downloader import AppleMusicBaseDownloader, AppleMusicDownloader, AppleMusicSongDownloader
    from gamdl.interface.enums import SongCodec, SyncedLyricsFormat, CoverFormat
except ImportError as e:
    logger.error(f"Failed to import gamdl modules: {e}")
    # We will let the app start so the user can use the WebUI to troubleshoot if needed.

app = FastAPI(title="Gamdl All-in-One", version="1.0.0")

# Global states
wrapper_proc: Optional[subprocess.Popen] = None
wrapper_api: Optional[WrapperApi] = None
apple_music_api: Optional[AppleMusicApi] = None

# Paths
WRAPPER_DIR = PROJECT_ROOT / "wrapper-v2"
ROOTFS_DIR = WRAPPER_DIR / "rootfs"
SYSTEM_LIBS_DIR = ROOTFS_DIR / "system" / "lib64"
DATA_DIR = PROJECT_ROOT / "data"
DATA_DIR.mkdir(exist_ok=True)

# Environment settings
HTTP_PORT = int(os.getenv("HTTP_PORT", "8080"))
TARGET_ARCH = os.getenv("TARGET_ARCH", "arm64-v8a")
if TARGET_ARCH == "amd64" or TARGET_ARCH == "x86_64":
    TARGET_ARCH = "x86_64"
else:
    TARGET_ARCH = "arm64-v8a"

class LoginRequest(BaseModel):
    username: str
    password: str

class Login2FARequest(BaseModel):
    code: str

# --- Helper Functions ---

async def start_wrapper_daemon():
    global wrapper_proc, wrapper_api, apple_music_api
    
    sentinel = SYSTEM_LIBS_DIR / "libandroidappmusic.so"
    if not sentinel.exists():
        logger.warning(f"Native Apple libraries not staged at {sentinel}. Wrapper daemon cannot start yet.")
        return False
        
    logger.info("Starting wrapper-v2 daemon...")
    
    env = os.environ.copy()
    env["HTTP_PORT"] = str(HTTP_PORT)
    env["TARGET_ARCH"] = TARGET_ARCH
    env["WRAPPER_BASE_DIR"] = str(DATA_DIR / "mpl_db")
    
    (DATA_DIR / "mpl_db").mkdir(exist_ok=True)
    
    # The wrapper binary requires rootfs/ to be in its working directory
    wrapper_bin = WRAPPER_DIR / "wrapper"
    if not wrapper_bin.exists():
        logger.error(f"Wrapper binary not found at {wrapper_bin}. Run build first.")
        return False
        
    try:
        wrapper_proc = subprocess.Popen(
            [str(wrapper_bin)],
            cwd=str(WRAPPER_DIR),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True
        )
        
        await asyncio.sleep(2)
        logger.info("wrapper-v2 daemon started successfully.")
        
        await init_apple_music_api()
        return True
    except Exception as e:
        logger.error(f"Failed to start wrapper daemon: {e}")
        return False

async def init_apple_music_api():
    global wrapper_api, apple_music_api
    try:
        wrapper_api = await WrapperApi.create(
            base_url=f"http://127.0.0.1:{HTTP_PORT}",
            get_credentials_func=None,
            get_2fa_code=None,
        )
        apple_music_api = await AppleMusicApi.create_from_wrapper(
            wrapper_api=wrapper_api,
            language="en-US",
        )
        logger.info("AppleMusicApi initialized from wrapper.")
    except Exception as e:
        logger.warning(f"Could not initialize AppleMusicApi (likely not logged in yet): {e}")

# --- API Endpoints ---

@app.on_event("startup")
async def startup_event():
    # Attempt to start the wrapper daemon on startup
    await start_wrapper_daemon()

@app.on_event("shutdown")
def shutdown_event():
    global wrapper_proc
    if wrapper_proc:
        logger.info("Stopping wrapper-v2 daemon...")
        wrapper_proc.terminate()
        wrapper_proc.wait()
        logger.info("wrapper-v2 daemon stopped.")

@app.get("/api/health")
async def health_check():
    staged = (SYSTEM_LIBS_DIR / "libandroidappmusic.so").exists()
    daemon_running = wrapper_proc is not None and wrapper_proc.poll() is None
    
    wrapper_healthy = False
    if daemon_running:
        try:
            import httpx
            async with httpx.AsyncClient() as client:
                res = await client.get(f"http://127.0.0.1:{HTTP_PORT}/health", timeout=2.0)
                if res.status_code == 200:
                    wrapper_healthy = True
        except Exception:
            pass
            
    return {
        "status": "ok",
        "staged": staged,
        "daemon_running": daemon_running,
        "wrapper_healthy": wrapper_healthy,
        "logged_in": apple_music_api is not None and apple_music_api.active_subscription
    }

@app.get("/api/me")
async def get_me():
    daemon_running = wrapper_proc is not None and wrapper_proc.poll() is None
    if not daemon_running:
        return {"logged_in": False, "error": "Wrapper daemon is not running"}
        
    try:
        import httpx
        async with httpx.AsyncClient() as client:
            res = await client.get(f"http://127.0.0.1:{HTTP_PORT}/me")
            return res.json()
    except Exception as e:
        return {"logged_in": False, "error": str(e)}

@app.post("/api/login")
async def login(req: LoginRequest):
    daemon_running = wrapper_proc is not None and wrapper_proc.poll() is None
    if not daemon_running:
        raise HTTPException(status_code=503, detail="Wrapper daemon is not running")
        
    try:
        import httpx
        async with httpx.AsyncClient() as client:
            res = await client.post(
                f"http://127.0.0.1:{HTTP_PORT}/login",
                json={"username": req.username, "password": req.password}
            )
            # Re-initialize API if login successful immediately (no 2FA)
            if res.status_code == 200:
                await init_apple_music_api()
            return JSONResponse(status_code=res.status_code, content=res.json())
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/api/login/2fa")
async def login_2fa(req: Login2FARequest):
    daemon_running = wrapper_proc is not None and wrapper_proc.poll() is None
    if not daemon_running:
        raise HTTPException(status_code=503, detail="Wrapper daemon is not running")
        
    try:
        import httpx
        async with httpx.AsyncClient() as client:
            res = await client.post(
                f"http://127.0.0.1:{HTTP_PORT}/login/2fa",
                json={"code": req.code}
            )
            if res.status_code == 200:
                await init_apple_music_api()
            return JSONResponse(status_code=res.status_code, content=res.json())
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/api/setup")
async def upload_apk(file: UploadFile = File(...)):
    temp_apk = DATA_DIR / file.filename
    try:
        with open(temp_apk, "wb") as buffer:
            shutil.copyfileobj(file.file, buffer)
            
        logger.info(f"Received APK file: {temp_apk.name}. Extracting libraries...")
        
        stage_cmd = ["bash", "tools/stage-system.sh", "--arch", TARGET_ARCH]
        stage_res = subprocess.run(stage_cmd, cwd=str(WRAPPER_DIR), capture_output=True, text=True)
        if stage_res.returncode != 0:
            logger.error(f"stage-system failed: {stage_res.stderr}")
            raise HTTPException(status_code=500, detail=f"Failed to stage system libs: {stage_res.stderr}")
            
        extract_cmd = [
            "bash", "tools/extract-libs.sh",
            "--bundle", str(temp_apk),
            "--arch", TARGET_ARCH,
            "--ignore-hash"
        ]
        extract_res = subprocess.run(extract_cmd, cwd=str(WRAPPER_DIR), capture_output=True, text=True)
        if extract_res.returncode != 0:
            logger.error(f"extract-libs failed: {extract_res.stderr}")
            raise HTTPException(status_code=500, detail=f"Failed to extract Apple libs: {extract_res.stderr}")
            
        temp_apk.unlink()
        
        global wrapper_proc
        if wrapper_proc:
            wrapper_proc.terminate()
            wrapper_proc.wait()
            
        success = await start_wrapper_daemon()
        return {"status": "success", "daemon_started": success}
    except Exception as e:
        if temp_apk.exists():
            temp_apk.unlink()
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/api/search")
async def search(q: str, type: str = "song", limit: int = 20):
    if not apple_music_api or not apple_music_api.active_subscription:
        raise HTTPException(status_code=401, detail="Apple Music subscription not authenticated")
        
    try:
        results = []
        if type == "song":
            res = await apple_music_api.get_search_results(q, limit=limit, types="songs")
            songs = res.get("results", {}).get("songs", {}).get("data", [])
            for s in songs:
                attrs = s["attributes"]
                results.append({
                    "id": s["id"],
                    "title": attrs["name"],
                    "artist": attrs["artistName"],
                    "album": attrs["albumName"],
                    "duration": int(attrs["durationInMillis"] / 1000),
                    "cover_url": attrs["artwork"]["url"].replace("{w}", "600").replace("{h}", "600"),
                    "track_number": attrs.get("trackNumber"),
                    "isrc": attrs.get("isrc")
                })
        elif type == "album":
            res = await apple_music_api.get_search_results(q, limit=limit, types="albums")
            albums = res.get("results", {}).get("albums", {}).get("data", [])
            for al in albums:
                attrs = al["attributes"]
                results.append({
                    "id": al["id"],
                    "title": attrs["name"],
                    "artist": attrs["artistName"],
                    "cover_url": attrs["artwork"]["url"].replace("{w}", "600").replace("{h}", "600"),
                    "release_date": attrs.get("releaseDate")
                })
        elif type == "artist":
            res = await apple_music_api.get_search_results(q, limit=limit, types="artists")
            artists = res.get("results", {}).get("artists", {}).get("data", [])
            for ar in artists:
                attrs = ar["attributes"]
                results.append({
                    "id": ar["id"],
                    "name": attrs["name"],
                    "url": attrs.get("url")
                })
        return results
    except Exception as e:
        logger.exception(f"Search failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/api/song/{track_id}")
async def get_song(track_id: str):
    if not apple_music_api or not apple_music_api.active_subscription:
        raise HTTPException(status_code=401, detail="Apple Music subscription not authenticated")
        
    try:
        res = await apple_music_api.get_song(track_id)
        song_data = res.get("data", [])
        if not song_data:
            raise HTTPException(status_code=404, detail="Song not found")
            
        s = song_data[0]
        attrs = s["attributes"]
        
        return {
            "id": s["id"],
            "title": attrs["name"],
            "artist": attrs["artistName"],
            "album": attrs["albumName"],
            "duration": int(attrs["durationInMillis"] / 1000),
            "track_number": attrs.get("trackNumber"),
            "disc_number": attrs.get("discNumber"),
            "isrc": attrs.get("isrc"),
            "cover_url": attrs["artwork"]["url"].replace("{w}", "1200").replace("{h}", "1200"),
            "release_date": attrs.get("releaseDate"),
            "copyright": attrs.get("copyright"),
            "composer": attrs.get("composerName"),
            "genre": attrs.get("genreNames", [None])[0] if attrs.get("genreNames") else None
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/api/stream/{track_id}")
async def stream_audio(track_id: str, quality: str = "alac-16-44"):
    if not apple_music_api or not apple_music_api.active_subscription:
        raise HTTPException(status_code=401, detail="Apple Music subscription not authenticated")
        
    temp_dir = DATA_DIR / f"temp_{track_id}"
    temp_dir.mkdir(exist_ok=True)
    
    try:
        codec_priority = [SongCodec.ALAC]
        if quality == "alac-16-44":
            codec_priority = [SongCodec.ALAC_16_44]
        elif quality == "alac-24-96":
            codec_priority = [SongCodec.ALAC_24_96]
        elif quality == "alac-24-192":
            codec_priority = [SongCodec.ALAC_24_192]
        elif quality == "aac-320":
            codec_priority = [SongCodec.AAC]
        elif quality == "aac-96":
            codec_priority = [SongCodec.AAC_HE]
            
        logger.info(f"Downloading stream for track {track_id} with codec {codec_priority[0].value}")
        
        base_interface = await AppleMusicBaseInterface.create(
            apple_music_api=apple_music_api,
            cover_format=CoverFormat.JPG,
            cover_size=1200,
            wrapper_api=wrapper_api,
        )
        
        song_interface = AppleMusicSongInterface(
            base=base_interface,
            synced_lyrics_format=SyncedLyricsFormat.LRC,
            codec_priority=codec_priority,
        )
        
        interface = AppleMusicInterface(
            song=song_interface,
        )
        
        base_downloader = AppleMusicBaseDownloader(
            interface=interface,
            output_path=str(temp_dir),
            temp_path=str(temp_dir),
            download_mode="ytdlp", # Use yt-dlp library mode inside container
            no_album_folder_template=True, # Save as flat filename
            no_album_file_template="{title_id}", # File named after track ID
        )
        
        song_downloader = AppleMusicSongDownloader(base=base_downloader)
        
        downloader = AppleMusicDownloader(
            song=song_downloader,
            overwrite=True,
            no_synced_lyrics=True,
        )
        
        url = f"https://music.apple.com/us/song/{track_id}"
        download_queue = []
        async for item in downloader.get_download_item_from_url(url):
            download_queue.append(item)
            
        if not download_queue:
            raise HTTPException(status_code=404, detail="Track not available or mismatch")
            
        item = download_queue[0]
        await downloader.download(item)
        
        m4a_path = Path(item.staged_path)
        if not m4a_path.exists():
            raise FileNotFoundError("M4A download file was not generated.")
            
        flac_path = temp_dir / f"{track_id}.flac"
        logger.info(f"Transcoding {m4a_path.name} to FLAC...")
        
        cmd = [
            "ffmpeg", "-y",
            "-i", str(m4a_path),
            "-c:a", "flac",
            "-map", "0:a",
            "-map", "0:v?",
            "-c:v", "copy",
            str(flac_path)
        ]
        
        res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if res.returncode != 0:
            logger.error(f"FFmpeg failed: {res.stderr.decode('utf-8')}")
            raise Exception("FFmpeg transcode failed")
            
        def cleanup():
            try:
                shutil.rmtree(temp_dir)
                logger.info(f"Cleaned up temp directory: {temp_dir}")
            except Exception as e:
                logger.error(f"Failed to cleanup temp files: {e}")
                
        return FileResponse(
            path=str(flac_path),
            media_type="audio/flac",
            filename=f"{track_id}.flac",
            background=BackgroundTasks().add_task(cleanup)
        )
        
    except Exception as e:
        logger.exception(f"Streaming failed for track {track_id}")
        if temp_dir.exists():
            shutil.rmtree(temp_dir)
        raise HTTPException(status_code=500, detail=f"Streaming failed: {str(e)}")

@app.get("/")
async def root():
    return {"name": "Gamdl All-in-One", "status": "online"}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)
