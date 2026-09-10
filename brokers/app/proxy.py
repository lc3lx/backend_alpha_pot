"""
One outbound proxy for the whole gateway process.

Brokers refuse this server's IP directly — Quotex answers with a Cloudflare challenge and
every poll comes back 429 — so broker traffic has to leave through a residential exit.

**Why this is set on the process rather than on the client.** The vendored Quotex library
has no proxy anywhere in its surface: `QuotexConfig` carries no such field, and its
transport builds `curl_cffi.requests.Session(impersonate=...)` with nothing passed in. So
there is no object to hand a proxy to. What its session does honour, because curl_cffi
trusts the environment like `requests` does, is the standard proxy variables — so those
are what get set, before any client is constructed.

Patching the vendored library would work too and was rejected: it is a git checkout that
gets re-cloned on every deploy, so the patch would silently disappear.
"""

from __future__ import annotations

import os

#: The variables libcurl and requests-style clients read. Both cases are set because
#: libcurl reads the lowercase forms and most Python clients read the uppercase ones.
_PROXY_VARS = (
    "HTTP_PROXY",
    "HTTPS_PROXY",
    "ALL_PROXY",
    "http_proxy",
    "https_proxy",
    "all_proxy",
)


def proxy_url() -> str | None:
    """
    The configured proxy as a URL, or None.

    Accepts either shape, because vendors hand out both:
        http://user:pass@host:port
        host:port:user:pass
    """
    raw = (os.environ.get("BROKER_PROXY") or os.environ.get("BINOLLA_AUTH_PROXY") or "").strip()
    if not raw:
        return None

    if "://" in raw:
        return raw

    parts = raw.split(":")
    if len(parts) >= 4:
        host, port, user, *rest = parts
        # The remainder is the password, kept whole so one containing ':' survives.
        return f"http://{user}:{':'.join(rest)}@{host}:{port}"
    if len(parts) == 2:
        return f"http://{raw}"
    return raw


def mask(url: str | None) -> str:
    """host:port only. Proxy credentials must never reach a log file."""
    if not url:
        return "none"
    tail = url.rsplit("@", 1)[-1]
    return tail or "set"


def configure_process_proxy() -> str | None:
    """
    Publish the proxy into the environment so every outbound client picks it up.

    Called before any broker client is built. An operator-set variable already in the
    environment wins — overriding it would silently ignore a deliberate override.
    """
    url = proxy_url()
    if not url:
        return None

    for name in _PROXY_VARS:
        if not os.environ.get(name):
            os.environ[name] = url

    # Localhost must not go through the proxy: the .NET API reaches this service on
    # 127.0.0.1, and routing that through a residential exit would break it.
    if not os.environ.get("NO_PROXY"):
        os.environ["NO_PROXY"] = "127.0.0.1,localhost"
    if not os.environ.get("no_proxy"):
        os.environ["no_proxy"] = os.environ["NO_PROXY"]

    return url
