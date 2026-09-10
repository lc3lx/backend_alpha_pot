"""
A real WebSocket transport for the Quotex client.

## Why

The vendored library's transport is called `CurlWebSocketTransport`, but it speaks no
WebSocket at all: it rewrites `transport=websocket` to `transport=polling` and then sends
every Socket.IO frame as a separate HTTP POST through `curl_cffi`. Quotex itself offers
the upgrade — its handshake answers `"upgrades":["websocket"]` — and the library declines
it.

Measured on the production log, that cost:

  * ~1 second per subscribe, because each frame is a fresh HTTPS round trip through the
    proxy — 25 pairs took 25+ seconds to arm
  * a constant stream of `{"code":1,"message":"Session ID unknown"}`, because a polling
    session dies to a 5-second ping timeout the moment a poll is late or exits from a
    different proxy IP
  * candles that never arrived, since the subscribes they were riding on were rejected

One open socket removes all three at once: frames go out immediately, the session is held
by the connection itself rather than by a sequence of matched requests, and pushes arrive
as the market prints them.

## How it is wired in

By replacing the class the library imports, not by editing the library. `vendor/QuotexAPI`
is a git checkout that is re-cloned on every deploy, so an edit there disappears silently
and the slow path comes back with nothing to explain why.

The interface below matches the original exactly — sync, same method names, same callback
registration — so every protocol behaviour above it is untouched.

## Cloudflare, and why the upgrade goes through curl_cffi

Quotex sits behind Cloudflare, which is why the library reached for `curl_cffi` in the
first place: it impersonates Chrome's TLS fingerprint, and Cloudflare's bot management
scores that fingerprint (JA3) as much as it scores the IP.

The first attempt here opened the socket with `websocket-client` and merely COPIED the
Cloudflare cookie across from a `curl_cffi` request. That cannot work, and the logs said
so — every upgrade came back 403 while the very same cookie fetch returned 200. A
`__cf_bm` cookie is issued to a fingerprint as well as an address, and presenting it from
a connection that plainly announces itself as Python is a worse signal than not
presenting it at all.

So the upgrade is made BY curl_cffi, on the impersonating session, and inherits the
fingerprint that already passes. `websocket-client` remains only as a fallback for a
curl_cffi too old to speak WebSocket; if neither works `connect()` returns False and the
library keeps its own polling transport rather than being left with a dead session.
"""

from __future__ import annotations

import json
import os
import queue
import threading
import time
from typing import Any, Callable
from urllib.parse import urlparse

from app.proxy import proxy_url

#: Engine.IO v3 packet types used here.
_OPEN = "0"
_PING = "2"
_PONG = "3"


def _enabled() -> bool:
    return (os.environ.get("QUOTEX_WS_TRANSPORT", "1") or "1").strip().lower() not in {
        "0",
        "false",
        "no",
    }


def _log(message: str) -> None:
    print(f"[WS] {message}", flush=True)


class WebSocketTransport:
    """
    Drop-in replacement for the library's polling transport.

    Deliberately synchronous and thread-based, because that is what it replaces: the
    library calls `connect`, `send` and `recv` from ordinary methods, and handing it
    coroutines would mean rewriting everything above it.
    """

    def __init__(self, url: str, headers: dict[str, str] | None = None) -> None:
        self.url = url
        self.headers = headers or {}
        self.sid: str | None = None
        self.message_queue: "queue.Queue[str]" = queue.Queue()

        self._ws: Any = None
        self._reader: threading.Thread | None = None
        self._pinger: threading.Thread | None = None
        self._running = threading.Event()
        self._ping_interval = 25.0

        self._on_message: Callable[[str], None] | None = None
        self._on_error: Callable[[Exception], None] | None = None
        self._on_close: Callable[[], None] | None = None

    # ---- registration (same names the library calls) -----------------------

    def set_on_message(self, callback: Callable[[str], None]) -> None:
        self._on_message = callback

    def set_on_error(self, callback: Callable[[Exception], None]) -> None:
        self._on_error = callback

    def set_on_close(self, callback: Callable[[], None]) -> None:
        self._on_close = callback

    # ---- lifecycle ---------------------------------------------------------

    def connect(self) -> bool:
        ws_url = _as_websocket_url(self.url)
        headers = dict(self.headers)
        headers.setdefault("Origin", _origin(ws_url))

        # Impersonating first. This is the transport that already gets 200s from
        # Cloudflare on the polling endpoint, so it is the one whose fingerprint the
        # challenge accepts.
        self._ws = _open_with_curl(ws_url, headers)
        if self._ws is None:
            self._ws = _open_with_websocket_client(ws_url, headers)
        if self._ws is None:
            return False

        # Engine.IO opens with 0{"sid":...}. Without it there is no session and nothing
        # below this would work, so a missing open packet is a failed connect.
        try:
            opening = self._ws.recv()
        except Exception as exc:
            _log(f"no open packet ({exc})")
            self._fail(exc)
            return False

        if not self._read_open(opening):
            _log(f"unexpected open packet: {str(opening)[:80]}")
            return False

        self._running.set()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()
        self._pinger = threading.Thread(target=self._ping_loop, daemon=True)
        self._pinger.start()

        _log(f"connected sid={self.sid} interval={self._ping_interval:.0f}s")
        return True

    def _read_open(self, packet: Any) -> bool:
        text = packet.decode() if isinstance(packet, (bytes, bytearray)) else str(packet)
        if not text.startswith(_OPEN):
            return False
        try:
            payload = json.loads(text[1:])
        except ValueError:
            return False

        self.sid = payload.get("sid")
        # Server-declared interval, in milliseconds. Pinging on its schedule rather than
        # a guess is what keeps the connection from being dropped as idle.
        interval = payload.get("pingInterval")
        if isinstance(interval, (int, float)) and interval > 0:
            self._ping_interval = float(interval) / 1000.0
        return bool(self.sid)

    def is_connected(self) -> bool:
        return bool(self._running.is_set() and self._ws is not None)

    def send(self, message: str) -> bool:
        if not self.is_connected():
            return False
        try:
            self._ws.send(message)
            return True
        except Exception as exc:
            self._fail(exc)
            return False

    def recv(self, timeout: float | None = None) -> str | None:
        try:
            return self.message_queue.get(timeout=timeout) if timeout else (
                self.message_queue.get_nowait()
            )
        except queue.Empty:
            return None

    def close(self) -> None:
        self._running.clear()
        ws, self._ws = self._ws, None
        if ws is not None:
            try:
                ws.close()
            except Exception:
                pass
        if self._on_close is not None:
            try:
                self._on_close()
            except Exception:
                pass

    # ---- loops -------------------------------------------------------------

    def _read_loop(self) -> None:
        while self._running.is_set() and self._ws is not None:
            try:
                frame = self._ws.recv()
            except Exception as exc:
                if self._running.is_set():
                    self._fail(exc)
                break

            if frame is None or frame == "":
                continue

            text = frame.decode() if isinstance(frame, (bytes, bytearray)) else str(frame)

            # Engine.IO housekeeping never reaches the protocol layer above.
            if text == _PONG:
                continue
            if text == _PING:
                # Some servers ping the client; answer or be dropped as unresponsive.
                try:
                    self._ws.send(_PONG)
                except Exception:
                    pass
                continue

            self.message_queue.put(text)
            if self._on_message is not None:
                try:
                    self._on_message(text)
                except Exception:
                    # A failing handler must not take the socket down with it.
                    pass

        self._running.clear()

    def _ping_loop(self) -> None:
        # A little under the server's interval: sending late is what gets a connection
        # closed as idle, and sending early costs nothing.
        while self._running.is_set():
            time.sleep(max(1.0, self._ping_interval * 0.8))
            if not self._running.is_set() or self._ws is None:
                break
            try:
                self._ws.send(_PING)
            except Exception as exc:
                self._fail(exc)
                break

    def _fail(self, exc: Exception) -> None:
        self._running.clear()
        if self._on_error is not None:
            try:
                self._on_error(exc)
            except Exception:
                pass


class _CurlSocket:
    """
    Adapts curl_cffi's WebSocket to the two calls this transport makes.

    curl_cffi has changed the shape of `recv` between releases — sometimes bytes,
    sometimes `(bytes, flags)` — so the result is normalised here rather than at every
    call site.
    """

    def __init__(self, session: Any, socket: Any) -> None:
        self._session = session
        self._socket = socket

    def recv(self) -> str:
        frame = self._socket.recv()
        if isinstance(frame, tuple):
            frame = frame[0]
        return frame.decode() if isinstance(frame, (bytes, bytearray)) else str(frame)

    def send(self, message: str) -> None:
        self._socket.send(message.encode())

    def close(self) -> None:
        try:
            self._socket.close()
        finally:
            try:
                self._session.close()
            except Exception:
                pass


def _open_with_curl(ws_url: str, headers: dict[str, str]) -> Any:
    """Opens the upgrade on an impersonating session, or None if that is not possible."""
    try:
        from curl_cffi import requests as curl_requests
    except ImportError:
        return None

    session_factory = getattr(curl_requests, "Session", None)
    if session_factory is None:
        return None

    try:
        session = session_factory(impersonate="chrome110")
    except Exception:
        return None

    proxy = proxy_url()
    if proxy:
        try:
            session.proxies = {"http": proxy, "https": proxy}
        except Exception:
            pass

    connect = getattr(session, "ws_connect", None)
    if connect is None:
        # curl_cffi predates WebSocket support. Not an error — the fallback handles it.
        _log("curl_cffi has no ws_connect; trying websocket-client")
        try:
            session.close()
        except Exception:
            pass
        return None

    try:
        socket = connect(ws_url, headers=headers)
    except Exception as exc:
        _log(f"impersonated upgrade refused ({_short(exc)})")
        try:
            session.close()
        except Exception:
            pass
        return None

    _log("upgraded on the impersonating session")
    return _CurlSocket(session, socket)


def _open_with_websocket_client(ws_url: str, headers: dict[str, str]) -> Any:
    """
    Last resort. Its TLS fingerprint is plainly Python, so Cloudflare usually refuses —
    but on a venue without bot management it works, and it costs nothing to try.
    """
    try:
        import websocket  # websocket-client
    except ImportError:
        _log("websocket-client is not installed either; leaving the polling transport")
        return None

    attempt = dict(headers)
    cookie = _cloudflare_cookies(ws_url)
    if cookie:
        attempt["Cookie"] = cookie
    attempt.setdefault(
        "User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36",
    )

    try:
        return websocket.create_connection(
            ws_url,
            header=[f"{k}: {v}" for k, v in attempt.items()],
            timeout=20,
            **_proxy_kwargs(),
        )
    except Exception as exc:
        proxy = _proxy_kwargs()
        via = f" via {proxy.get('proxy_type')} proxy" if proxy else " directly"
        _log(f"plain upgrade refused{via} ({_short(exc)}); falling back to polling")
        return None


def _short(exc: Exception) -> str:
    """Cloudflare's refusal carries a full header dump; the first line is the useful part."""
    text = str(exc).replace("\n", " ")
    return text[:160]


# ---- helpers ---------------------------------------------------------------


def _as_websocket_url(url: str) -> str:
    """The library hands over an http/polling URL; this needs the ws/websocket form."""
    ws = url.replace("https://", "wss://").replace("http://", "ws://")
    if "transport=" in ws:
        ws = ws.replace("transport=polling", "transport=websocket")
    elif "?" in ws:
        ws = f"{ws}&EIO=3&transport=websocket"
    else:
        ws = f"{ws}?EIO=3&transport=websocket"
    return ws


def _origin(ws_url: str) -> str:
    host = urlparse(ws_url).netloc
    return f"https://{host}"


def _cloudflare_cookies(ws_url: str) -> str | None:
    """
    Collects Cloudflare's clearance cookies with a browser-impersonating request.

    A raw WebSocket handshake has none of the TLS fingerprint Cloudflare looks for, so it
    is challenged and refused. Fetching the same origin through `curl_cffi` first — the
    library's own trick — earns the cookies, and presenting them on the upgrade is what
    lets it through.
    """
    try:
        from curl_cffi import requests as curl_requests
    except ImportError:
        return None

    http_url = ws_url.replace("wss://", "https://").replace("ws://", "http://")
    http_url = http_url.replace("transport=websocket", "transport=polling")

    try:
        session = curl_requests.Session(impersonate="chrome110")
        proxy = proxy_url()
        if proxy:
            session.proxies = {"http": proxy, "https": proxy}
        session.get(http_url, timeout=15)
        jar = getattr(session, "cookies", None)
        if not jar:
            return None
        pairs = [f"{name}={value}" for name, value in jar.items()]
        return "; ".join(pairs) if pairs else None
    except Exception:
        # Cookies are an optimisation for getting past the challenge, not a requirement.
        return None


def _proxy_kwargs() -> dict[str, Any]:
    """
    websocket-client takes the proxy split into parts rather than as a URL.

    The scheme decides `proxy_type`. Sending a SOCKS5 proxy as `http` makes the client
    speak HTTP CONNECT at a SOCKS listener, which fails in a way that looks like the
    broker refusing us rather than a misconfiguration.
    """
    url = proxy_url()
    if not url:
        return {}

    parsed = urlparse(url)
    if not parsed.hostname:
        return {}

    scheme = (parsed.scheme or "http").lower()
    if scheme.startswith("socks"):
        # websocket-client needs PySocks for these; without it the connection fails with
        # an import error rather than a network one.
        proxy_type = "socks5h" if scheme in {"socks5h", "socks5"} else scheme
        default_port = 1080
    else:
        proxy_type = "http"
        default_port = 8080

    kwargs: dict[str, Any] = {
        "http_proxy_host": parsed.hostname,
        "http_proxy_port": parsed.port or default_port,
        "proxy_type": proxy_type,
    }
    if parsed.username:
        kwargs["http_proxy_auth"] = (parsed.username, parsed.password or "")
    return kwargs


def install() -> bool:
    """
    Points the library's connection service at this transport.

    Returns whether the swap happened, so start-up can say plainly which transport is in
    use instead of leaving it to be inferred from latency.
    """
    if not _enabled():
        _log("disabled by QUOTEX_WS_TRANSPORT; leaving the library's polling transport")
        return False

    try:
        from QuotexAPI.services import connection as connection_module
    except ImportError:
        return False

    if not hasattr(connection_module, "CurlWebSocketTransport"):
        _log("connection service has no transport to replace; library layout changed")
        return False

    connection_module.CurlWebSocketTransport = WebSocketTransport  # type: ignore[attr-defined]
    _log("websocket transport installed")
    return True
