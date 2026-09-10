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

## Cloudflare

Quotex sits behind Cloudflare, which is the reason the library reached for `curl_cffi`:
it impersonates a browser's TLS fingerprint. A plain WebSocket handshake does not. So the
Cloudflare cookies are collected first with `curl_cffi` and presented on the WebSocket
handshake — the same trick the browser performs. If Cloudflare still refuses, `connect()`
returns False and the caller falls back to the original transport rather than leaving the
session dead.
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
        try:
            import websocket  # websocket-client
        except ImportError:
            _log("websocket-client is not installed; falling back to polling")
            return False

        ws_url = _as_websocket_url(self.url)
        headers = dict(self.headers)

        cookie = _cloudflare_cookies(ws_url)
        if cookie:
            headers["Cookie"] = cookie
        headers.setdefault("Origin", _origin(ws_url))
        headers.setdefault(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36",
        )

        try:
            self._ws = websocket.create_connection(
                ws_url,
                header=[f"{k}: {v}" for k, v in headers.items()],
                timeout=20,
                **_proxy_kwargs(),
            )
        except Exception as exc:
            _log(f"handshake refused ({exc}); falling back to polling")
            self._fail(exc)
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
    """websocket-client takes the proxy split into parts rather than as a URL."""
    url = proxy_url()
    if not url:
        return {}

    parsed = urlparse(url)
    if not parsed.hostname:
        return {}

    kwargs: dict[str, Any] = {
        "http_proxy_host": parsed.hostname,
        "http_proxy_port": parsed.port or 8080,
        "proxy_type": "http",
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
