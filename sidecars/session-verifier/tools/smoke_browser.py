"""Exercise the complete browser flow with intercepted fixture responses only."""
import asyncio
import base64
from urllib.parse import parse_qs, quote, urlsplit

from nodriver import cdp
from session_verifier.browser import verify


async def main():
    async def respond(page, event):
        url = event.request.url
        content_type = "text/html"
        if urlsplit(url).path.endswith("/challenge/verify"):
            content_type = "application/json"
            body = '{"grant":"fixture-grant"}'
        elif urlsplit(url).hostname == "challenges.cloudflare.com":
            body = '<button style="position:fixed;inset:0" onclick="parent.postMessage(\'verified\',\'*\')">Fixture</button>'
        else:
            body = '''<iframe src="https://challenges.cloudflare.com/fixture" width="300" height="65"></iframe>
            <script>addEventListener('message', () => fetch('/v2/challenge/verify', {method:'POST'}));</script>'''
        await page.send(cdp.fetch.fulfill_request(event.request_id, 200,
            response_headers=[cdp.fetch.HeaderEntry("Content-Type", content_type)],
            body=base64.b64encode(body.encode()).decode()))

    callback = "spotiflac://session-grant?state=fixture&cb_version=v2grant"
    url = "https://api.zarz.moe/v2/challenge?id=fixture&cb=" + quote(callback, safe="")
    result = await verify(url, allowed_request_handler=respond)
    assert parse_qs(urlsplit(result).query)["grant"] == ["fixture-grant"]
    print("Chromium fixture challenge, click, grant capture and cleanup passed")


asyncio.run(asyncio.wait_for(main(), 40))
