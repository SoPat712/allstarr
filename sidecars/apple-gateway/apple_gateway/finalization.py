from __future__ import annotations

import asyncio
import contextlib
import hashlib
from pathlib import Path

from mutagen.flac import FLAC, SeekPoint, SeekTable
from mutagen.mp4 import MP4


@contextlib.asynccontextmanager
async def _process(executable, *arguments):
    process = await asyncio.create_subprocess_exec("nice", "-n", "19", str(executable), *arguments,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
    try:
        yield process
        if await process.wait() != 0:
            raise ValueError("audio_finalization_failed")
    finally:
        if process.returncode is None:
            with contextlib.suppress(ProcessLookupError):
                process.kill()
        await process.wait()


async def finalize_audio(source: Path, target: Path, ffmpeg: str):
    with source.open("rb") as stream:
        flac = stream.read(4) == b"fLaC"
    if not flac:
        tags = MP4(source)
        timing = []
        if tags.info.codec == "alac":
            # Older demuxers omit the last packet duration, but its decoded sample count is exact.
            frames, samples, last = 0, 0, 0
            async with _process(Path(ffmpeg).with_name("ffprobe"), "-v", "error", "-threads", "1",
                    "-select_streams", "a:0", "-show_frames", "-show_entries", "frame=nb_samples",
                    "-of", "csv=p=0", str(source)) as process:
                async for line in process.stdout:
                    last = int(line.strip())
                    if last <= 0:
                        raise ValueError("audio_finalization_failed")
                    frames += 1
                    samples += last
                    if samples > tags.info.sample_rate * 86400:
                        raise ValueError("audio_finalization_failed")
            if not frames:
                raise ValueError("audio_finalization_failed")
            timing = ["-bsf:a", f"setts=duration='if(eq(N,{frames - 1}),{last},DURATION)'"]
        async with _process(ffmpeg, "-nostdin", "-v", "error", "-xerror", "-threads", "1",
                "-i", str(source), "-map", "0:a:0", "-c:a", "copy", "-threads", "1",
                *timing, "-movflags", "+faststart", "-f", "mp4", "-n", str(target)):
            pass
        output = MP4(target)
        output.update(tags)
        output.save()
        if output.info.length <= 0:
            raise ValueError("audio_finalization_failed")
        return

    tags = FLAC(source)
    width = (tags.info.bits_per_sample + 7) // 8
    if tags.info.bits_per_sample not in {8, 16, 24, 32}:
        raise ValueError("audio_finalization_failed")
    table = SeekTable(b"")
    total = 0
    first_position = None
    ffprobe = str(Path(ffmpeg).with_name("ffprobe"))
    async with _process(ffprobe, "-v", "error", "-threads", "1", "-select_streams", "a:0",
            "-show_packets", "-show_entries", "packet=pts,duration,pos", "-of", "compact=p=0", str(source)) as process:
        async for line in process.stdout:
            packet = dict(field.split("=", 1) for field in line.decode().strip().split("|") if "=" in field)
            sample, count, position = (int(packet[name]) for name in ("pts", "duration", "pos"))
            if sample != total or count <= 0 or count > 65535:
                raise ValueError("audio_finalization_failed")
            if first_position is None:
                first_position = position
            if not table.seekpoints or sample - table.seekpoints[-1].first_sample >= tags.info.sample_rate * 10:
                table.seekpoints.append(SeekPoint(sample, position - first_position, count))
            total += count
            if total > tags.info.sample_rate * 86400:
                raise ValueError("audio_finalization_failed")
    digest = hashlib.md5()
    decoded = 0
    codec = "pcm_u8" if width == 1 else f"pcm_s{width * 8}le"
    async with _process(ffmpeg, "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode",
            "-threads", "1", "-filter_threads", "1", "-i", str(source), "-map", "0:a:0", "-threads", "1",
            "-c:a", codec, "-f", "u8" if width == 1 else f"s{width * 8}le", "pipe:1") as process:
        while chunk := await process.stdout.read(64 * 1024):
            if width == 1:
                chunk = bytes(value ^ 128 for value in chunk)
            digest.update(chunk)
            decoded += len(chunk)
    if not total or total >= 1 << 36 or decoded != total * tags.info.channels * width:
        raise ValueError("audio_finalization_failed")
    tags.info.total_samples = total
    tags.info.md5_signature = int.from_bytes(digest.digest(), "big")
    blocks = [block for block in tags.metadata_blocks if block.code not in {1, 3}]
    blocks.append(table)
    with source.open("rb") as input, target.open("xb") as output:
        output.write(b"fLaC")
        for index, block in enumerate(blocks):
            data = block.write()
            output.write(bytes([block.code | (128 if index == len(blocks) - 1 else 0)])
                + len(data).to_bytes(3, "big") + data)
        input.seek(first_position)
        while chunk := input.read(64 * 1024):
            output.write(chunk)
            await asyncio.sleep(0)
