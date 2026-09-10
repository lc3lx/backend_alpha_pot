"""
The WebSocket transport's own logic.

The socket itself cannot be tested without Quotex, but everything around it can — and
these are the parts that decide whether the connection is even attempted correctly: the
URL rewrite, the Engine.IO open packet, the ping schedule, and the proxy split.
"""

from __future__ import annotations

import pytest

from app.quotex_ws import WebSocketTransport, _as_websocket_url, _proxy_kwargs


@pytest.fixture(autouse=True)
def _clean_proxy_env(monkeypatch):
    for name in ("BROKER_PROXY", "BINOLLA_AUTH_PROXY", "BROKER_PROXY_SCHEME"):
        monkeypatch.delenv(name, raising=False)


def test_the_polling_url_is_rewritten_to_websocket():
    """
    The library hands over its polling URL. Left alone, this would open a second polling
    session — the exact thing being replaced.
    """
    url = "https://ws2.qxbroker.com/socket.io/?EIO=3&transport=polling"
    assert _as_websocket_url(url) == (
        "wss://ws2.qxbroker.com/socket.io/?EIO=3&transport=websocket"
    )


def test_a_bare_url_gains_the_websocket_parameters():
    assert _as_websocket_url("https://ws2.qxbroker.com/socket.io/") == (
        "wss://ws2.qxbroker.com/socket.io/?EIO=3&transport=websocket"
    )


def test_the_open_packet_sets_the_session_and_ping_schedule():
    transport = WebSocketTransport("wss://example/socket.io/")

    ok = transport._read_open(  # noqa: SLF001
        '0{"sid":"abc123","upgrades":[],"pingInterval":25000,"pingTimeout":5000}'
    )

    assert ok is True
    assert transport.sid == "abc123"
    # Milliseconds on the wire, seconds here. Getting this wrong by 1000x either hammers
    # the server or lets the connection be dropped as idle.
    assert transport._ping_interval == pytest.approx(25.0)  # noqa: SLF001


def test_a_packet_that_is_not_an_open_is_rejected():
    transport = WebSocketTransport("wss://example/socket.io/")
    assert transport._read_open('42["hello"]') is False  # noqa: SLF001
    assert transport._read_open("0not-json") is False  # noqa: SLF001


def test_bytes_open_packets_are_accepted():
    transport = WebSocketTransport("wss://example/socket.io/")
    assert transport._read_open(b'0{"sid":"xyz","pingInterval":20000}') is True  # noqa: SLF001
    assert transport.sid == "xyz"


def test_a_fresh_transport_is_not_connected():
    assert WebSocketTransport("wss://example/socket.io/").is_connected() is False


def test_sending_before_connecting_fails_quietly():
    # The library checks the return value; raising here would surface as a crash rather
    # than a reconnect.
    assert WebSocketTransport("wss://example/socket.io/").send('42["x"]') is False


def test_recv_on_an_empty_queue_returns_none():
    assert WebSocketTransport("wss://example/socket.io/").recv(timeout=0.01) is None


def test_the_proxy_is_split_into_the_parts_the_client_needs(monkeypatch):
    """websocket-client takes host/port/auth separately, not a URL."""
    monkeypatch.setenv("BROKER_PROXY", "proxy.example.net:9000:user-ae:s3cr3t")

    kwargs = _proxy_kwargs()

    assert kwargs["http_proxy_host"] == "proxy.example.net"
    assert kwargs["http_proxy_port"] == 9000
    assert kwargs["http_proxy_auth"] == ("user-ae", "s3cr3t")
    assert kwargs["proxy_type"] == "http"


def test_no_proxy_means_no_proxy_arguments(monkeypatch):
    monkeypatch.delenv("BROKER_PROXY", raising=False)
    monkeypatch.delenv("BINOLLA_AUTH_PROXY", raising=False)
    assert _proxy_kwargs() == {}


def test_socks5_is_passed_as_socks_not_http(monkeypatch):
    """
    Sending a SOCKS proxy as `http` makes the client speak HTTP CONNECT at a SOCKS
    listener. That fails looking like the broker refusing us, not a misconfiguration.
    """
    monkeypatch.setenv("BROKER_PROXY", "socks5://user:pass@proxy.example.net:1080")

    kwargs = _proxy_kwargs()

    assert kwargs["proxy_type"] == "socks5h"
    assert kwargs["http_proxy_host"] == "proxy.example.net"
    assert kwargs["http_proxy_port"] == 1080
    assert kwargs["http_proxy_auth"] == ("user", "pass")


def test_a_socks_proxy_without_a_port_uses_the_socks_default(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", "socks5://proxy.example.net")
    assert _proxy_kwargs()["http_proxy_port"] == 1080


def test_an_http_proxy_still_reports_http(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", "http://proxy.example.net:9000")
    assert _proxy_kwargs()["proxy_type"] == "http"


def test_origin_is_the_site_not_the_socket_host():
    """
    A browser opening wss://ws2.qxbroker.com from the trading site sends the SITE as the
    origin. Sending the socket host is a combination no browser produces — and the upgrade
    was refused 403 while ordinary requests to the same host passed.
    """
    from app.quotex_ws import _origin

    assert _origin("wss://ws2.qxbroker.com/socket.io/?EIO=3") == "https://qxbroker.com"
    assert _origin("wss://ws.example.co/socket.io/") == "https://example.co"
    # Already the bare site: nothing to strip.
    assert _origin("wss://qxbroker.com/socket.io/") == "https://qxbroker.com"
