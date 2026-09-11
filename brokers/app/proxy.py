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


#: Schemes understood here. `socks5h` resolves DNS at the proxy, which is what you want
#: through a residential exit — resolving locally leaks the lookup and can land on a
#: different CDN edge than the one the traffic reaches.
_SCHEMES = {"http", "https", "socks5", "socks5h", "socks4"}


def proxy_scheme() -> str:
    """
    Which protocol to speak to the proxy.

    <para>SOCKS5 is worth preferring where the provider offers it: it is CONNECTION-
    oriented, so one tunnel carries the whole WebSocket from a single exit IP. An HTTP
    proxy re-resolves per request, which on a rotating residential pool means a different
    IP for the Cloudflare cookie than for the upgrade that presents it — and a 403.</para>
    """
    raw = (os.environ.get("BROKER_PROXY_SCHEME") or "").strip().lower()
    if raw in _SCHEMES:
        # socks5 without the h resolves locally; the remote form is nearly always meant.
        return "socks5h" if raw == "socks5" else raw
    return "http"


def proxy_url() -> str | None:
    """
    The configured proxy as a URL, or None.

    Accepts either shape, because vendors hand out both:
        socks5://user:pass@host:port   (or http://)
        host:port:user:pass

    A URL keeps its own scheme. The bare host:port form takes BROKER_PROXY_SCHEME, so
    switching a whole deployment between HTTP and SOCKS5 is one environment variable
    rather than a rewritten credential line.
    """
    raw = (os.environ.get("BROKER_PROXY") or os.environ.get("BINOLLA_AUTH_PROXY") or "").strip()
    if not raw:
        return None

    if "://" in raw:
        return raw

    scheme = proxy_scheme()
    parts = raw.split(":")
    if len(parts) >= 4:
        host, port, user, *rest = parts
        if "quantumproxies.io" in host and port == "12000" and not os.environ.get("BROKER_PROXY_SCHEME"):
            scheme = "socks5h"
        elif "quantumproxies.io" in host and port == "10000" and not os.environ.get("BROKER_PROXY_SCHEME"):
            scheme = "http"
        # The remainder is the password, kept whole so one containing ':' survives.
        return f"{scheme}://{user}:{':'.join(rest)}@{host}:{port}"
    if len(parts) == 2:
        return f"{scheme}://{raw}"
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


def check_stickiness(samples: int = 3) -> dict[str, object]:
    """
    Reports whether the proxy keeps one exit IP across requests.

    A rotating proxy breaks every session-based protocol: Cloudflare issues its clearance
    cookie to the IP that asked for it, so a WebSocket upgrade from a different IP is
    refused 403, and a Socket.IO session opened on one IP is unknown on the next. Both
    failures look like broker or code problems from the outside — the 403 arrives with a
    normal-looking Cloudflare page, and the session error says only "Session ID unknown".

    Diagnosing that from those symptoms cost several rounds. Asking directly costs one
    request, so it is asked at start-up and the answer is stated plainly.
    """
    url = proxy_url()
    if not url:
        return {"proxy": "none", "sticky": None, "exits": []}

    try:
        import httpx
    except ImportError:
        return {"proxy": mask(url), "sticky": None, "exits": []}

    exits: list[str] = []
    for _ in range(max(2, samples)):
        try:
            with httpx.Client(proxy=url, timeout=10) as client:
                exits.append(client.get("https://api.ipify.org").text.strip())
        except Exception:
            # A failed sample says nothing either way; a partial answer is still useful.
            continue

    unique = sorted(set(exits))
    sticky = len(unique) == 1 if len(exits) >= 2 else None
    return {"proxy": mask(url), "sticky": sticky, "exits": unique}
