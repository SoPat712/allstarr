from __future__ import annotations

import asyncio
import os
import sys

import pytest

from apple_gateway.runner import ProcessFailure
from apple_gateway.workers import WorkerPool
from test_gateway import settings


@pytest.fixture
def command(tmp_path):
    script = tmp_path / "worker.py"
    script.write_text('''
import json, os, struct, sys, time
count = 0
def send(kind, value):
    data = value if isinstance(value, bytes) else json.dumps(value).encode()
    sys.stdout.buffer.write(kind + struct.pack(">I", len(data)) + data)
    sys.stdout.buffer.flush()
for line in sys.stdin:
    request = json.loads(line)
    if request["song_id"] == "crash":
        os._exit(1)
    if request["song_id"] == "stuck":
        time.sleep(60)
    if request["song_id"] == "failure":
        send(b"F", {"code": "segment_download_failed"})
        continue
    count += 1
    send(b"H", {"media_type": "audio/flac", "suffix": ".flac"})
    send(b"D", f"{os.getpid()}:{count}:{request['song_id']}".encode())
    if request["song_id"] == "flood":
        for _ in range(100):
            send(b"D", b"x" * 65536)
    if request["song_id"] == "tail":
        time.sleep(60)
    send(b"E", {})
''')
    return [sys.executable, str(script)]


async def consume(pool, song, token="fixture-token"):
    async with pool.open({"song_id": song, "token": token, "storefront": "us"}) as (_, chunks):
        return b"".join([chunk async for chunk in chunks]).decode().split(":")


@pytest.mark.asyncio
async def test_worker_reuses_process_for_multiple_songs(settings, command):
    pool = WorkerPool(settings, command=command)
    try:
        first = await consume(pool, "101")
        second = await consume(pool, "102")
        assert first[0] == second[0]
        assert first[1:] == ["1", "101"]
        assert second[1:] == ["2", "102"]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_dead_and_timed_out_workers_are_replaced(settings, command):
    pool = WorkerPool(settings, command=command)
    try:
        first = await consume(pool, "101")
        with pytest.raises(ProcessFailure, match="worker_failed"):
            await consume(pool, "crash")
        second = await consume(pool, "102")
        assert first[0] != second[0]
        with pytest.raises(ProcessFailure, match="process_timeout"):
            await consume(pool, "stuck")
        third = await consume(pool, "103")
        assert second[0] != third[0]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_last_reader_cancellation_kills_worker_and_releases_slot(settings, command):
    pool = WorkerPool(settings, command=command)
    try:
        async with pool.open({"song_id": "tail", "token": "fixture-token", "storefront": "us"}) as (_, chunks):
            first = (await anext(chunks)).decode().split(":")
        with pytest.raises(ProcessLookupError):
            os.kill(int(first[0]), 0)
        second = await consume(pool, "102")
        assert first[0] != second[0]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_worker_limit_applies_until_stream_completion(settings, command):
    pool = WorkerPool(settings, command=command)
    try:
        async with pool.open({"song_id": "tail", "token": "fixture-token", "storefront": "us"}) as (_, chunks):
            first = (await anext(chunks)).decode().split(":")
            pending = asyncio.create_task(consume(pool, "102"))
            await asyncio.sleep(0)
            assert not pending.done()
        second = await asyncio.wait_for(pending, 3)
        assert first[0] != second[0]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_worker_reports_stage_failure_and_next_song_can_recover(settings, command):
    pool = WorkerPool(settings, command=command)
    try:
        with pytest.raises(ProcessFailure, match="segment_download_failed"):
            await consume(pool, "failure")
        assert (await consume(pool, "102"))[1:] == ["1", "102"]
    finally:
        await pool.close()


@pytest.mark.asyncio
async def test_cancellation_drains_full_stdout_pipe_before_reaping_worker(settings, command):
    pool = WorkerPool(settings, command=command)

    async def abandon():
        async with pool.open({"song_id": "flood", "token": "fixture-token", "storefront": "us"}) as (_, chunks):
            await anext(chunks)
            await asyncio.sleep(0.05)

    try:
        await asyncio.wait_for(abandon(), 3)
        assert (await consume(pool, "102"))[1:] == ["1", "102"]
    finally:
        await pool.close()
