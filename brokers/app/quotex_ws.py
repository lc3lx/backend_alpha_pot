"""Synchronous Quotex transport with a curl_cffi WebSocket and polling fallback.

Network operations are called through asyncio.to_thread by the protocol adapter.
Commands use text frames; polling packets use Engine.IO v3 length prefixes.
Upgrade attempts are bounded and backed off after repeated failures. The plain
websocket-client fallback is opt-in because it adds another TLS handshake.

This module is installed outside vendor/ so deployments retain the fixes.
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

#: Consecutive refusals before the upgrade is left alone for a while.
#:
#: Cloudflare refuses this venue's upgrade for reasons outside our reach, and retrying it
#: on every connect spends the timeout below each time — turning a broker that WOULD work
#: over polling into one that takes half a minute to reach. Three attempts is enough to
#: tell a refusal from a blip.
_MAX_CONSECUTIVE_FAILURES = 3

#: How long to stay on polling after giving up. Long enough not to matter, short enough
#: that a venue which starts allowing upgrades is picked up the same day.
_BACKOFF_SECONDS = 30

#: Longest a single upgrade attempt may take. A broker either accepts the handshake
#: quickly or is not going to; residential proxy routing needs adequate headroom.
_CONNECT_TIMEOUT = 25

#: Engine.IO v3 packet types used here.
_OPEN = "0"
_PING = "2"
_PONG = "3"


def _enabled() -> bool:
    return os.environ.get("BROKER_QUOTEX_WS_TRANSPORT",
        os.environ.get("QUOTEX_WS_TRANSPORT", "1")).strip().lower() not in {
        "0",
        "false",
        "no",
    }


#: The transport this module replaced. Kept so a refused upgrade can hand the session
#: back to it rather than failing outright.
_original_transport: Any = None


def _log(message: str) -> None:
    print(f"[WS] {message}", flush=True)


class _UpgradeHealth:
    """Remembers whether upgrades are worth attempting on this host."""

    def __init__(self) -> None:
        self._failures = 0
        self._skip_until = 0.0

    def should_try(self) -> bool:
        if time.monotonic() < self._skip_until:
            return False
        return True

    def record_success(self) -> None:
        self._failures = 0
        self._skip_until = 0.0

    def record_failure(self) -> None:
        self._failures += 1
        if self._failures < _MAX_CONSECUTIVE_FAILURES:
            return

        self._skip_until = time.monotonic() + _BACKOFF_SECONDS
        self._failures = 0
        _log(
            f"upgrade refused {_MAX_CONSECUTIVE_FAILURES}x; staying on polling for "
            f"{_BACKOFF_SECONDS} seconds"
        )


_health = _UpgradeHealth()


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
        #: Set when the upgrade is refused and the original transport takes over. Every
        #: method below forwards to it, so the library never learns which one it got.
        self._delegate: Any = None
        self._reader: threading.Thread | None = None
        self._pinger: threading.Thread | None = None
        self._running = threading.Event()
        self._stopped = threading.Event()
        self._send_lock = threading.Lock()
        self._ping_interval = 25.0

        self._on_message: Callable[[str], None] | None = None
        self._on_error: Callable[[Exception], None] | None = None
        self._on_close: Callable[[], None] | None = None

    # ---- registration (same names the library calls) -----------------------

    def set_on_message(self, callback: Callable[[str], None]) -> None:
        self._on_message = callback
        if self._delegate is not None:
            self._delegate.set_on_message(callback)

    def set_on_error(self, callback: Callable[[Exception], None]) -> None:
        self._on_error = callback
        if self._delegate is not None:
            self._delegate.set_on_error(callback)

    def set_on_close(self, callback: Callable[[], None]) -> None:
        self._on_close = callback
        if self._delegate is not None:
            self._delegate.set_on_close(callback)

    # ---- lifecycle ---------------------------------------------------------

    def connect(self) -> bool:
        if not _health.should_try():
            # Already established that this host refuses upgrades. Skipping straight to
            # the transport that does work is the difference between a connect taking a
            # second and taking half a minute.
            return self._connect_via_original()

        ws_url = _as_websocket_url(self.url)
        headers = dict(self.headers)
        # The set a browser actually sends on an upgrade. Cloudflare scores the shape of
        # a request, not only its TLS: an upgrade missing the Sec-Fetch trio and carrying
        # the wrong Origin looks like no browser it has ever seen.
        origin = _origin(ws_url)
        headers.setdefault("Origin", origin)
        headers.setdefault("Referer", f"{origin}/")
        headers.setdefault("Sec-Fetch-Dest", "websocket")
        headers.setdefault("Sec-Fetch-Mode", "websocket")
        headers.setdefault("Sec-Fetch-Site", "same-site")
        headers.setdefault("Accept-Language", "en-US,en;q=0.9")
        headers.setdefault("Cache-Control", "no-cache")
        headers.setdefault("Pragma", "no-cache")

        # Impersonating first. This is the transport that already gets 200s from
        # Cloudflare on the polling endpoint, so it is the one whose fingerprint the
        # challenge accepts.
        self._ws = _open_with_curl(ws_url, headers)
        if self._ws is None and os.getenv("QUOTEX_WS_PLAIN_FALLBACK", "0") == "1":
            self._ws = _open_with_websocket_client(ws_url, headers)
        if self._ws is None:
            _health.record_failure()
            # Refused — hand the session to the transport this replaced. Returning False
            # here is what killed the venue outright instead of merely slowing it down.
            return self._connect_via_original()

        # Engine.IO opens with 0{"sid":...}. Without it there is no session and nothing
        # below this would work, so a missing open packet is a failed connect.
        try:
            opening = self._ws.recv()
        except Exception as exc:
            _log(f"no open packet ({exc})")
            self.close()
            _health.record_failure()
            return self._connect_via_original()

        if not self._read_open(opening):
            _log("unexpected open packet")
            self.close()
            _health.record_failure()
            return self._connect_via_original()

        self.message_queue.put(opening)
        self._stopped.clear()
        self._running.set()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()
        self._pinger = threading.Thread(target=self._ping_loop, daemon=True)
        self._pinger.start()

        _health.record_success()
        _log(f"connected interval={self._ping_interval:.0f}s")
        return True

    def _connect_via_original(self) -> bool:
        """Opens the session on the transport this module replaced."""
        if _original_transport is None:
            _log("no original transport to fall back to; the session cannot open")
            return False

        try:
            delegate = _original_transport(self.url, self.headers)
        except Exception as exc:
            _log(f"fallback transport could not be built ({_short(exc)})")
            return False

        # Hand over any callbacks registered before the fallback existed, or the library
        # would receive nothing on a connection it believes is live.
        if self._on_message is not None:
            delegate.set_on_message(self._on_message)
        if self._on_error is not None:
            delegate.set_on_error(self._on_error)
        if self._on_close is not None:
            delegate.set_on_close(self._on_close)

        if hasattr(delegate, "_poll_messages"):
            # Own the polling loop too: the vendor otherwise retries expired SIDs
            # forever while continuing to report is_connected() == True.
            delegate._poll_messages = lambda: self._poll_loop(delegate)

        try:
            opened = bool(delegate.connect())
        except Exception as exc:
            _log(f"fallback transport failed to connect ({_short(exc)})")
            return False

        if not opened:
            return False

        self._delegate = delegate
        self.sid = getattr(delegate, "sid", None)
        self._stopped.clear()
        self._running.set()
        self._pinger = threading.Thread(target=self._ping_loop, daemon=True)
        self._pinger.start()
        _log("running on the original transport (polling)")
        return True

    def _read_open(self, packet: Any) -> bool:
        text = packet.decode() if isinstance(packet, (bytes, bytearray)) else str(packet)
        if not text.startswith(_OPEN):
            return False
        try:
            payload = json.loads(text[1:])
        except ValueError:
            return False

        if not isinstance(payload, dict):
            return False

        self.sid = payload.get("sid")
        # Server-declared interval, in milliseconds. Pinging on its schedule rather than
        # a guess is what keeps the connection from being dropped as idle.
        interval = payload.get("pingInterval")
        if isinstance(interval, (int, float)) and interval > 0:
            self._ping_interval = float(interval) / 1000.0
        return bool(self.sid)

    def is_connected(self) -> bool:
        if self._delegate is not None:
            return bool(self._delegate.is_connected())
        return bool(self._running.is_set() and self._ws is not None)

    def send(self, message: str) -> bool:
        if self._delegate is not None:
            with self._send_lock:
                if hasattr(self._delegate, "polling_url"):
                    return self._send_polling(message)
                try:
                    res = self._delegate.send(message)
                    return res is not False
                except Exception as exc:
                    self._fail(exc)
                    return False
        if not self.is_connected():
            return False
        try:
            with self._send_lock:
                self._ws.send(message)
            return True
        except Exception as exc:
            self._fail(exc)
            return False

    def _send_polling(self, message: str) -> bool:
        delegate = self._delegate
        if not delegate.is_connected():
            return False
        # Engine.IO v3 polling POSTs carry length-prefixed packets. The length is
        # measured in JavaScript UTF-16 code units, not UTF-8 bytes.
        size = len(message.encode("utf-16-le")) // 2
        try:
            response = delegate.session.post(
                f"{delegate.polling_url}&sid={delegate.sid}",
                headers={**self.headers, "Content-Type": "text/plain;charset=UTF-8"},
                data=f"{size}:{message}", timeout=6)
            if response.status_code != 200:
                raise ConnectionError(f"Polling send failed: HTTP {response.status_code}")
            return True
        except Exception as exc:
            delegate.close()
            self._fail(exc)
            return False

    def _poll_loop(self, delegate: Any) -> None:
        while delegate.is_connected():
            try:
                response = delegate.session.get(
                    f"{delegate.polling_url}&sid={delegate.sid}",
                    headers=self.headers, timeout=25)
                if response.status_code != 200:
                    raise ConnectionError(f"Polling receive failed: HTTP {response.status_code}")
                for packet in delegate._parse_polling_payload(response.text):
                    delegate.message_queue.put(packet)
            except Exception as exc:
                delegate.close()
                self._fail(exc)
                break

    def recv(self, timeout: float | None = None) -> str | None:
        if self._delegate is not None:
            return self._delegate.recv(timeout)
        try:
            return self.message_queue.get(timeout=timeout) if timeout else (
                self.message_queue.get_nowait()
            )
        except queue.Empty:
            return None

    def close(self) -> None:
        self._running.clear()
        self._stopped.set()
        delegate, self._delegate = self._delegate, None
        if delegate is not None:
            try:
                delegate.close()
            except Exception:
                pass
            return

        self._running.clear()
        self._stopped.set()
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
                self._fail(ConnectionError("WebSocket closed by peer"))
                break

            text = frame.decode() if isinstance(frame, (bytes, bytearray)) else str(frame)

            # Engine.IO housekeeping never reaches the protocol layer above.
            if text == _PONG:
                continue
            if text == _PING:
                # Some servers ping the client; answer or be dropped as unresponsive.
                try:
                    self.send(_PONG)
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
            if self._stopped.wait(max(1.0, self._ping_interval * 0.8)):
                break
            if not self._running.is_set() or not self.is_connected():
                break
            try:
                if not self.send(_PING):
                    break
            except Exception as exc:
                self._fail(exc)
                break

    def _fail(self, exc: Exception) -> None:
        self._running.clear()
        self._stopped.set()
        ws, self._ws = self._ws, None
        if ws is not None:
            try:
                ws.close()
            except Exception:
                pass
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
        self._opening = True

    def recv(self) -> str:
        # Synchronous curl_cffi.recv has no timeout argument. Bound the initial
        # Engine.IO packet by closing the socket if its handshake never arrives.
        timer = threading.Timer(_CONNECT_TIMEOUT, self.close) if self._opening else None
        if timer is not None:
            timer.daemon = True
            timer.start()
        try:
            frame = self._socket.recv()
        finally:
            self._opening = False
            if timer is not None:
                timer.cancel()
        if isinstance(frame, tuple):
            if int(frame[1]) & 8:  # CurlWsFlag.CLOSE
                return ""
            frame = frame[0]
        return frame.decode() if isinstance(frame, (bytes, bytearray)) else str(frame)

    def send(self, message: str) -> None:
        # curl_cffi defaults to BINARY for bytes, but Socket.IO commands are TEXT.
        if hasattr(self._socket, "send_str"):
            self._socket.send_str(message)
        elif hasattr(self._socket, "send"):
            try:
                from curl_cffi.curl import CurlWsFlag
                self._socket.send(message, CurlWsFlag.TEXT)
            except Exception:
                self._socket.send(message.encode("utf-8") if isinstance(message, str) else message)

    def close(self) -> None:
        try:
            self._socket.close()
        finally:
            try:
                self._session.close()
            except Exception:
                pass


def _open_with_curl(ws_url: str, headers: dict[str, str]) -> Any:
    """
    Opens the upgrade on an impersonating session, or None if that is not possible.

    The session is WARMED with an ordinary request first. Cloudflare issues `__cf_bm` on a
    connection's first request and expects it back on the rest; a cold WebSocket upgrade
    carries no such cookie and is refused 403 — which is exactly what happened here even
    from a dedicated static IP with Chrome's TLS fingerprint. The library's polling path
    never hit this because it always GETs before it POSTs, so the cookie was already in
    its jar.

    Warming on the SAME session is what matters. An earlier attempt fetched the cookie on
    one session and replayed it on another, which is a worse signal than sending none.
    """
    try:
        from curl_cffi import requests as curl_requests
    except ImportError:
        return None

    session_factory = getattr(curl_requests, "Session", None)
    if session_factory is None:
        return None

    # Bounded. Without a timeout an upgrade against a misconfigured proxy hung for FIVE
    # MINUTES before giving up — holding a connect slot the whole time, which is what
    # turned one bad setting into a storm of refused sign-ins everywhere else.
    try:
        session = session_factory(impersonate="chrome120", timeout=_CONNECT_TIMEOUT)
    except TypeError:
        # Older curl_cffi takes the timeout per request rather than per session.
        try:
            session = session_factory(impersonate="chrome120")
        except Exception:
            return None
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
        _log("curl_cffi has no ws_connect; trying websocket-client")
        try:
            session.close()
        except Exception:
            pass
        return None

    # Strip custom User-Agent so curl_cffi uses its matched TLS browser profile
    ws_headers = {k: v for k, v in headers.items() if k.lower() != "user-agent"}

    extra_kwargs: dict[str, Any] = {
        "impersonate": "chrome120",
    }
    if proxy:
        extra_kwargs["proxy"] = proxy

    # 1. Warm session first with polling GET to earn Cloudflare cookies
    _warm_session(session, ws_url, extra_kwargs)
    if hasattr(session, "cookies") and session.cookies:
        try:
            if hasattr(session.cookies, "items"):
                cookie_items = [f"{k}={v}" for k, v in session.cookies.items()]
            elif hasattr(session.cookies, "jar"):
                cookie_items = [f"{c.name}={c.value}" for c in session.cookies.jar]
            else:
                cookie_items = [f"{k}={v}" for k, v in dict(session.cookies).items()]
            if cookie_items and "Cookie" not in ws_headers and "cookie" not in ws_headers:
                ws_headers["Cookie"] = "; ".join(cookie_items)
        except Exception:
            pass

    # 2. Connect directly to ws_url with the earned cookies (DO NOT append sid=!)
    try:
        try:
            socket = connect(ws_url, headers=ws_headers, timeout=_CONNECT_TIMEOUT, **extra_kwargs)
        except TypeError:
            socket = connect(ws_url, headers=ws_headers, **extra_kwargs)
        _log("upgraded on the impersonating session (warmed)")
        return _CurlSocket(session, socket)
    except Exception as exc_warmed:
        _log(f"warmed upgrade failed ({_short(exc_warmed)}); trying direct without warm-up")

    # 3. Fallback: try raw connect without warming
    try:
        try:
            socket = connect(ws_url, headers=ws_headers, timeout=_CONNECT_TIMEOUT, **extra_kwargs)
        except TypeError:
            socket = connect(ws_url, headers=ws_headers, **extra_kwargs)
        _log("upgraded on the impersonating session (direct)")
        return _CurlSocket(session, socket)
    except Exception as exc:
        _log(f"impersonated upgrade refused ({_short(exc)})")
        try:
            session.close()
        except Exception:
            pass
        return None


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


def _warm_session(session: Any, ws_url: str, extra_kwargs: dict[str, Any] | None = None) -> str | None:
    """
    Earns Cloudflare's clearance cookie on this session, so the upgrade carries it.

    Failure is not fatal: some deployments answer the upgrade without one, and the
    attempt costs a single request either way.
    """
    http_url = ws_url.replace("wss://", "https://").replace("ws://", "http://")
    http_url = http_url.replace("transport=websocket", "transport=polling")

    origin = _origin(ws_url)
    req_kwargs = dict(extra_kwargs or {})
    try:
        resp = session.get(
            http_url,
            timeout=_CONNECT_TIMEOUT,
            headers={"Origin": origin, "Referer": f"{origin}/en/trade"},
            verify=False,
            **req_kwargs,
        )
        text = resp.text
        if resp.status_code == 200 and ("sid" in text):
            try:
                idx = text.find("{")
                end_idx = text.rfind("}")
                if idx != -1 and end_idx != -1:
                    data = json.loads(text[idx:end_idx + 1])
                    return data.get("sid")
            except Exception:
                pass
    except Exception as exc:
        _log(f"session warm-up failed ({_short(exc)}); trying the upgrade anyway")
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
    """
    The page's origin, not the socket's host.

    A browser opening `wss://ws2.qxbroker.com/...` from the trading site sends
    `Origin: https://qxbroker.com` — the document it is running in. Sending the socket
    host instead is a combination no browser produces, and it is a difference Cloudflare
    can see: the polling requests were passing while the upgrade alone came back 403.
    """
    host = urlparse(ws_url).netloc.split(":")[0]
    labels = host.split(".")
    # ws2.qxbroker.com -> qxbroker.com; a bare two-label host is already the site.
    site = ".".join(labels[-2:]) if len(labels) > 2 else host
    return f"https://{site}"


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
        session = curl_requests.Session(impersonate="chrome120")
        proxy = proxy_url()
        if proxy:
            session.proxies = {"http": proxy, "https": proxy}
        session.get(http_url, timeout=15, verify=False)
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

    global _original_transport
    if _original_transport is None:
        # Captured BEFORE the swap, and only once: installing twice would otherwise make
        # the fallback point at this class and recurse.
        _original_transport = connection_module.CurlWebSocketTransport

    connection_module.CurlWebSocketTransport = WebSocketTransport  # type: ignore[attr-defined]
    _log("websocket transport installed")
    return True
