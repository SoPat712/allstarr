import asyncio
import base64
import contextlib
import json
import logging
import os
import sys
import tempfile

from .protocol import allowed_resource, callback_with_grant, canonical_resource_url, challenge_context, grant_from_response


async def close_browser(browser):
    process = getattr(browser, "_process", None)
    try:
        browser.stop()
    finally:
        if process is not None:
            try:
                await asyncio.wait_for(process.communicate(), timeout=5)
            except TimeoutError:
                with contextlib.suppress(ProcessLookupError):
                    process.kill()
                await process.communicate()
        await asyncio.sleep(0)


async def verify(url: str, allowed_request_handler=None) -> str:
    import nodriver
    from nodriver import cdp

    callback, verify_url = challenge_context(url)
    browser = None
    logging.disable(logging.CRITICAL)
    with tempfile.TemporaryDirectory(prefix="session-browser-") as profile:
        try:
            browser = await nodriver.start(
                browser_executable_path=os.environ.get("CHROME_PATH", "/usr/bin/chromium"),
                user_data_dir=profile,
                headless=False,
                sandbox=False,
                browser_args=["--disable-dev-shm-usage", "--window-size=1280,900", "--no-first-run"],
            )
            page = await browser.get("about:blank")
            result = asyncio.get_running_loop().create_future()
            requests = set()

            async def route(event):
                if allowed_resource(event.request.url):
                    if allowed_request_handler is None:
                        await page.send(cdp.fetch.continue_request(event.request_id))
                    else:
                        await allowed_request_handler(page, event)
                else:
                    await page.send(cdp.fetch.fail_request(event.request_id, cdp.network.ErrorReason.BLOCKED_BY_CLIENT))

            async def response(event):
                if canonical_resource_url(event.response.url) == verify_url and event.response.status == 200:
                    requests.add(event.request_id)

            async def completed(event):
                if event.request_id not in requests or result.done():
                    return
                requests.discard(event.request_id)
                try:
                    body, encoded = await page.send(cdp.network.get_response_body(event.request_id))
                    if len(body) > 24 * 1024:
                        return
                    if encoded:
                        body = base64.b64decode(body, validate=True).decode("utf-8")
                    grant = grant_from_response(verify_url, verify_url, body)
                    if grant and not result.done():
                        result.set_result(callback_with_grant(callback, grant))
                except (ValueError, TypeError, nodriver.core.connection.ProtocolException):
                    pass

            page.add_handler(cdp.fetch.RequestPaused, route)
            page.add_handler(cdp.network.ResponseReceived, response)
            page.add_handler(cdp.network.LoadingFinished, completed)
            await page.send(cdp.fetch.enable(patterns=[cdp.fetch.RequestPattern(url_pattern="*")]))
            await page.send(cdp.network.enable())
            # Reattaching a tab clears its handlers; retain interception while navigating.
            await page.send(cdp.page.navigate(url))

            def frames(node):
                if node.node_name == "IFRAME":
                    attributes = dict(zip((node.attributes or [])[::2], (node.attributes or [])[1::2]))
                    if attributes.get("src", "").startswith("https://challenges.cloudflare.com/"):
                        yield node
                for child in [*(node.children or []), *(node.shadow_roots or [])]:
                    yield from frames(child)
                if node.content_document:
                    yield from frames(node.content_document)

            for _ in range(3):
                for tick in range(40):
                    if result.done():
                        return result.result()
                    if tick in (8, 24):
                        try:
                            document = await page.send(cdp.dom.get_document(depth=-1, pierce=True))
                            for frame in frames(document):
                                box = await page.send(cdp.dom.get_box_model(node_id=frame.node_id))
                                left, top, right, _, _, bottom, *_ = box.border
                                if right - left < 40 or bottom - top < 20:
                                    continue
                                x, y = left + 28, (top + bottom) / 2
                                await page.send(cdp.input_.dispatch_mouse_event("mouseMoved", x, y))
                                await page.send(cdp.input_.dispatch_mouse_event("mousePressed", x, y, button=cdp.input_.MouseButton.LEFT, click_count=1))
                                await page.send(cdp.input_.dispatch_mouse_event("mouseReleased", x, y, button=cdp.input_.MouseButton.LEFT, click_count=1))
                                break
                        except nodriver.core.connection.ProtocolException:
                            pass
                    await asyncio.sleep(0.5)
                await page.send(cdp.page.navigate(url))
            raise TimeoutError("verification_timeout")
        finally:
            if browser is not None:
                await close_browser(browser)


def main():
    try:
        payload = json.loads(sys.stdin.read(16 * 1024 + 1))
        # The browser library may print diagnostics containing challenge material.
        with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet), contextlib.redirect_stderr(quiet):
            callback = asyncio.run(asyncio.wait_for(verify(payload["verification_url"]), timeout=90))
        print(json.dumps({"callback_url": callback}))
    except Exception:
        print(json.dumps({"error": "verification_unavailable"}))
        sys.exit(1)


if __name__ == "__main__":
    main()
