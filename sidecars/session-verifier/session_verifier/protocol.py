import json
import re
from urllib.parse import parse_qs, urlencode, urlsplit, urlunsplit

SERVICE_HOST = "api.zarz.moe"
RESOURCE_HOSTS = {SERVICE_HOST, "challenges.cloudflare.com"}
MAXIMUM_BYTES = 16 * 1024


def allowed_resource(url: str) -> bool:
    try:
        parsed = urlsplit(url)
        return (
            parsed.scheme == "https"
            and parsed.hostname in RESOURCE_HOSTS
            and parsed.port in (None, 443)
            and parsed.username is None
            and parsed.password is None
        )
    except ValueError:
        return False


def challenge_context(url: str) -> tuple[str, str]:
    if not isinstance(url, str) or len(url) > 8192 or not allowed_resource(url):
        raise ValueError("unsupported_challenge")
    parsed = urlsplit(url)
    if parsed.hostname != SERVICE_HOST or not re.fullmatch(r"/(?:v[0-9]+/)?challenge", parsed.path):
        raise ValueError("unsupported_challenge")
    query = parse_qs(parsed.query)
    if len(query.get("id", [])) != 1 or not query["id"][0] or len(query.get("cb", [])) != 1:
        raise ValueError("unsupported_challenge")
    callback = query["cb"][0]
    target = urlsplit(callback)
    state = parse_qs(target.query).get("state", [])
    if target.scheme != "spotiflac" or target.netloc != "session-grant" or len(state) != 1 or not state[0]:
        raise ValueError("unsupported_callback")
    verify_url = urlunsplit(("https", SERVICE_HOST, parsed.path + "/verify", "", ""))
    return callback, verify_url


def canonical_resource_url(url: str) -> str | None:
    if not allowed_resource(url):
        return None
    parsed = urlsplit(url)
    return urlunsplit(("https", parsed.hostname, parsed.path, parsed.query, ""))


def callback_with_grant(callback: str, grant: str) -> str:
    if not isinstance(grant, str) or not grant.strip() or len(grant) > 8192 or any(ord(c) < 32 for c in grant):
        raise ValueError("invalid_grant")
    parsed = urlsplit(callback)
    query = parse_qs(parsed.query)
    query["grant"] = [grant.strip()]
    return urlunsplit((parsed.scheme, parsed.netloc, parsed.path, urlencode(query, doseq=True), ""))


def grant_from_response(url: str, verify_url: str, body: str) -> str | None:
    if canonical_resource_url(url) != verify_url or len(body) > MAXIMUM_BYTES:
        return None
    try:
        payload = json.loads(body)
        grant = payload.get("grant") if isinstance(payload, dict) else None
        return grant if isinstance(grant, str) and grant.strip() else None
    except (ValueError, TypeError):
        return None


PAGE_LINK = re.compile(r"spotiflac://session-grant/?\?[^\s\"'<>]+")


def grant_from_page(text: str, callback: str) -> str | None:
    """The challenge page also shows the finished sign-in link for people whose app didn't open; read its grant."""
    if not isinstance(text, str) or len(text) > 64 * 1024:
        return None
    expected = parse_qs(urlsplit(callback).query).get("state", [])
    for match in PAGE_LINK.finditer(text):
        target = urlsplit(match.group(0))
        query = parse_qs(target.query)
        grants = query.get("grant", [])
        if (target.scheme == "spotiflac" and target.netloc == "session-grant" and expected and query.get("state") == expected
                and len(grants) == 1 and grants[0].strip()):
            return grants[0]
    return None
