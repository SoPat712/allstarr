from __future__ import annotations

import asyncio
import json
import shutil
import subprocess
import sys
from contextlib import asynccontextmanager
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from gamdl.interface.types import MediaTags

from apple_gateway.progressive import _decrypt, audio_chunks, boxes, clear_init
from apple_gateway.finalization import _process
from apple_gateway.runner import AudioCeiling, BoundedProcessRunner, ProcessFailure, ProcessResult
from apple_gateway.security import diagnostic_tail
from test_gateway import ACCOUNT, settings


@pytest.fixture
def damaged_alac(tmp_path):
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        pytest.skip("ffmpeg is required")
    source = tmp_path / "damaged.m4a"
    subprocess.run([ffmpeg, "-v", "error", "-filter_threads", "1", "-f", "lavfi", "-i",
        "anoisesrc=sample_rate=44100:duration=2:seed=1", "-ac", "2", "-c:a", "alac", "-threads", "1",
        "-sample_fmt", "s32p", "-movflags", "+empty_moov+default_base_moof", "-frag_duration", "200000",
        str(source)], check=True, capture_output=True)
    packets = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", str(source)],
        check=True, capture_output=True).stdout)["packets"]
    packet = packets[len(packets) // 2]
    raw = bytearray(source.read_bytes())
    start, size = int(packet["pos"]), int(packet["size"])
    raw[start:start + size] = b"\x80" * size
    source.write_bytes(raw)
    return ffmpeg, source


@pytest.mark.asyncio
@pytest.mark.parametrize("ceiling", [None, (16, 44100)])
async def test_recoverable_later_alac_frame_matches_tolerant_file_conversion(settings, tmp_path, damaged_alac, ceiling):
    ffmpeg, source = damaged_alac
    raw = source.read_bytes()
    parts = [raw[start:stop] for _, start, stop, _ in boxes(raw)]
    init = b"".join(part for part in parts if part[4:8] in {b"ftyp", b"moov"})
    assert clear_init(init)[1] == ("alac", 24, 44100)
    conversion = [] if ceiling is None else ["-ar", "44100", "-sample_fmt", "s16"]
    expected = tmp_path / "whole.flac"
    tolerant = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-y", "-threads", "1",
        "-i", str(source), "-map", "0:a:0", *conversion, "-threads", "1", "-c:a", "flac", str(expected)],
        capture_output=True)
    assert tolerant.returncode == 0 and b"Error" in tolerant.stderr
    strict = subprocess.run([ffmpeg, "-v", "error", "-xerror", "-err_detect", "explode", "-threads", "1",
        "-i", str(source), "-threads", "1", "-f", "null", "-"], capture_output=True)
    assert strict.returncode != 0
    release = asyncio.Event()
    later = [index for index, part in enumerate(parts) if part[4:8] == b"moof"][3]

    async def fragments():
        for index, part in enumerate(parts):
            if index == later:
                await release.wait()
            yield part

    media = SimpleNamespace(media_id="101", tags=MediaTags(), cover_bytes=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(drm_free=True)))
    chunks = audio_chunks(media, None, replace(settings, ffmpeg_path=ffmpeg), tmp_path,
        source=fragments(), ceiling=ceiling)
    await anext(chunks)
    try:
        first = await asyncio.wait_for(anext(chunks), 3)
        assert first and not release.is_set()
        release.set()
        result = tmp_path / "progressive.flac"
        result.write_bytes(first + b"".join([chunk async for chunk in chunks]))
    finally:
        release.set()
        await chunks.aclose()
    decoded = []
    for path in (expected, result):
        decoded.append(subprocess.run([ffmpeg, "-v", "error", "-xerror", "-threads", "1", "-i", str(path),
            "-threads", "1", "-f", "s32le", "-"], check=True, capture_output=True).stdout)
    assert decoded[0] == decoded[1]


class FailingPool:
    def __init__(self, after_first=False):
        self.after_first = after_first
        self.calls = 0
        self.closed = False

    @asynccontextmanager
    async def open(self, request):
        self.calls += 1
        try:
            async def chunks():
                if self.after_first:
                    yield b"fLaCprefix"
                raise ProcessFailure("transcode_failed", stderr=(
                    "decoder failed https://private.invalid/file?token=value " + ACCOUNT.token))
            yield {"media_type": "audio/flac", "suffix": ".flac"}, chunks()
        finally:
            self.closed = True


def recovery_runner(settings, tmp_path, after_first=False):
    runner = BoundedProcessRunner(settings)
    pool = FailingPool(after_first)
    runner._audio_workers = pool

    async def download(url, quality, output, temporary, **kwargs):
        assert pool.closed
        output.mkdir(parents=True)
        source = output / "source.m4a"
        source.write_bytes(b"source")
        return [source]

    async def convert(source, target, ceiling=None):
        target.write_bytes(b"fLaCcomplete")
        return target

    runner.download = AsyncMock(side_effect=download)
    runner.audio_codec = AsyncMock(return_value="alac")
    runner.exceeds_ceiling = AsyncMock(return_value=True)
    runner.seekable_flac = AsyncMock(side_effect=convert)
    runner.audio_duration = AsyncMock(return_value=2.0)
    runner.validate_audio = AsyncMock()
    return runner, pool


async def consume(runner, song="101"):
    async with runner.open_audio(song, "alac", media_user_token=ACCOUNT.token, storefront="us",
            ceiling=AudioCeiling(16, 44100)) as (_, chunks):
        return b"".join([chunk async for chunk in chunks])


@pytest.mark.asyncio
async def test_failure_before_audio_falls_back_in_same_request_and_logs_stderr(settings, tmp_path, caplog):
    runner, pool = recovery_runner(settings, tmp_path)
    assert await consume(runner) == b"fLaCcomplete"
    assert pool.calls == 1 and runner.download.await_count == 1
    runner.validate_audio.assert_awaited_once()
    assert runner.seekable_flac.call_args.args[2] == AudioCeiling(16, 44100)
    assert "song=101" in caplog.text and "stage=transcode" in caplog.text and "decoder failed" in caplog.text
    assert "private.invalid" not in caplog.text and ACCOUNT.token not in caplog.text
    assert not list((settings.data_root / "temporary").glob("audio-*"))


@pytest.mark.asyncio
async def test_failure_after_audio_aborts_then_retry_uses_file_until_expiry(settings, tmp_path, monkeypatch, caplog):
    clock = [100.0]
    monkeypatch.setattr("apple_gateway.runner.time.monotonic", lambda: clock[0])
    runner, pool = recovery_runner(settings, tmp_path, after_first=True)
    async with runner.open_audio("101", "alac", media_user_token=ACCOUNT.token, storefront="us") as (_, chunks):
        assert await anext(chunks) == b"fLaCprefix"
        with pytest.raises(ProcessFailure, match="transcode_failed"):
            await anext(chunks)
    assert runner.download.await_count == 0
    assert await consume(runner) == b"fLaCcomplete"
    assert pool.calls == 1
    clock[0] += 3601
    with pytest.raises(ProcessFailure):
        await consume(runner)
    assert pool.calls == 2
    assert "decoder failed" in caplog.text and "stage=transcode" in caplog.text


@pytest.mark.asyncio
@pytest.mark.parametrize("method", ["to_flac", "seekable_flac", "stream_flac"])
async def test_file_encoder_failure_keeps_stderr(settings, tmp_path, method):
    runner = BoundedProcessRunner(settings)
    source = tmp_path / "source.m4a"
    source.write_bytes(b"input")
    runner.execute = AsyncMock(return_value=ProcessResult(1, "", "decoder failure"))
    if method == "stream_flac":
        process = SimpleNamespace(returncode=1, pid=0, stdout=SimpleNamespace(read=AsyncMock(return_value=b"")),
            stderr=SimpleNamespace(read=AsyncMock(side_effect=[b"decoder failure", b""])), wait=AsyncMock(return_value=1))
        with pytest.MonkeyPatch.context() as patch:
            patch.setattr(asyncio, "create_subprocess_exec", AsyncMock(return_value=process))
            with pytest.raises(ProcessFailure) as failure:
                _ = [chunk async for chunk in runner.stream_flac(source)]
    else:
        with pytest.raises(ProcessFailure) as failure:
            await getattr(runner, method)(source, tmp_path / "output.flac")
    assert failure.value.stderr == "decoder failure"


def test_diagnostics_are_bounded_and_remove_urls_tokens_and_keys():
    tail = diagnostic_tail("x" * 10000 + "\nAuthorization: Bearer private-token\n"
        "decrypt key=00112233445566778899aabbccddeeff\n"
        "decode skd://private.invalid/path https://private.invalid/path fixture-token", "fixture-token")
    assert len(tail) <= 2048
    assert all(secret not in tail for secret in ("private-token", "00112233", "private.invalid", "fixture-token"))
    assert "decode" in tail


@pytest.mark.asyncio
async def test_fragment_decrypt_failure_retains_native_diagnostics_and_cleans_files(tmp_path, monkeypatch):
    media = SimpleNamespace(media_id="101", decryption_key=None,
        stream_info=SimpleNamespace(audio_track=SimpleNamespace(fairplay_key="fixture-key", use_single_content_key=True)))
    monkeypatch.setattr("gamdl.downloader.ammuxer.decrypt_and_mux_wrapper",
        AsyncMock(side_effect=RuntimeError("decrypt packet failed")))
    with pytest.raises(ProcessFailure) as failure:
        await _decrypt(b"init", b"moof", b"mdat", tmp_path, media, SimpleNamespace(wrapper_api=object()))
    assert failure.value.stderr == "decrypt packet failed" and failure.value.stage == "decrypt"
    assert not list(tmp_path.iterdir())


@pytest.mark.asyncio
async def test_failure_in_whole_file_fallback_is_logged_and_never_returns_audio(settings, tmp_path, caplog):
    runner, _ = recovery_runner(settings, tmp_path)
    runner.seekable_flac.side_effect = ProcessFailure("transcode_failed", stderr="file encoder stopped")
    with pytest.raises(ProcessFailure, match="transcode_failed"):
        await consume(runner)
    assert "file encoder stopped" in caplog.text and "song=101" in caplog.text
    assert not list((settings.data_root / "temporary").glob("audio-*"))


@pytest.mark.asyncio
async def test_file_download_failure_logs_diagnostics_before_caller_can_retry(settings, tmp_path, caplog):
    runner = BoundedProcessRunner(settings)
    runner.execute = AsyncMock(return_value=ProcessResult(1, "", "decrypt packet failed"))
    with pytest.raises(ProcessFailure):
        await runner.download("https://music.apple.com/us/song/101", "alac", tmp_path / "output",
            tmp_path / "download", media_user_token=ACCOUNT.token)
    assert "song=101" in caplog.text and "stage=decrypt_mux" in caplog.text
    assert "decrypt packet failed" in caplog.text


@pytest.mark.asyncio
async def test_retry_memory_is_bounded_and_shared_with_streaming_recovery(settings, tmp_path):
    runner, pool = recovery_runner(settings, tmp_path)
    for song in range(300):
        runner.remember_file_retry(str(song))
    assert len(runner._file_retries) == 256
    assert "0" not in runner._file_retries and "299" in runner._file_retries
    pool.closed = True
    assert await consume(runner, "299") == b"fLaCcomplete"
    assert pool.calls == 0


@pytest.mark.asyncio
async def test_account_failure_does_not_fall_back_or_remember_song(settings, tmp_path):
    runner, _ = recovery_runner(settings, tmp_path)

    @asynccontextmanager
    async def denied(request):
        raise ProcessFailure("account_mismatch")
        yield

    runner._audio_workers.open = denied
    with pytest.raises(ProcessFailure, match="account_mismatch"):
        await consume(runner)
    assert not runner._file_retries
    runner.download.assert_not_awaited()


@pytest.mark.asyncio
async def test_fallback_replays_damaged_input_through_real_file_converter(settings, tmp_path, damaged_alac):
    ffmpeg, source = damaged_alac
    if shutil.which("metaflac") is None:
        pytest.skip("metaflac is required")
    runner = BoundedProcessRunner(replace(settings, ffmpeg_path=ffmpeg))
    pool = FailingPool()
    runner._audio_workers = pool

    async def download(url, quality, output, temporary, **kwargs):
        assert pool.closed
        output.mkdir(parents=True)
        artifact = output / "source.m4a"
        shutil.copyfile(source, artifact)
        return [artifact]

    runner.download = download
    result = tmp_path / "fallback.flac"
    result.write_bytes(await consume(runner))
    await runner.validate_audio(result)
    facts = json.loads(subprocess.run([str(Path(ffmpeg).with_name("ffprobe")), "-v", "error",
        "-show_entries", "stream=sample_rate,bits_per_raw_sample", "-of", "json", str(result)],
        check=True, capture_output=True).stdout)["streams"][0]
    assert int(facts["sample_rate"]) == 44100 and int(facts["bits_per_raw_sample"]) == 16
    assert not list((settings.data_root / "temporary").glob("audio-*"))


@pytest.mark.asyncio
async def test_finalization_process_failure_retains_bounded_stderr():
    with pytest.raises(ProcessFailure) as failure:
        async with _process(sys.executable, "-c",
                "import sys; sys.stderr.write('x' * 40000 + '\\nremux failed'); sys.exit(1)"):
            pass
    assert failure.value.stage == "finalize"
    assert failure.value.stderr.endswith("remux failed") and len(failure.value.stderr) <= 32768
