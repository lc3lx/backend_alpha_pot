"""
The outbound proxy.

Brokers refuse this server's IP, so a proxy that is configured but not actually applied
is indistinguishable from a broken account: Quotex answers a Cloudflare challenge and
every request comes back 429. That failure already cost a full debugging session, so the
wiring is pinned here.
"""

from __future__ import annotations

import pytest

from app.proxy import configure_process_proxy, mask, proxy_url

VENDOR_LINE = "proxy.example.net:9000:user-country-ae:s3cr3t"
VENDOR_URL = "http://user-country-ae:s3cr3t@proxy.example.net:9000"


@pytest.fixture(autouse=True)
def clean_env(monkeypatch):
    for name in (
        "BROKER_PROXY",
        "BINOLLA_AUTH_PROXY",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
    ):
        monkeypatch.delenv(name, raising=False)


def test_vendor_host_port_user_pass_form_becomes_a_url(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    assert proxy_url() == VENDOR_URL


def test_a_password_containing_a_colon_survives(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", "host:9000:user:pa:ss:word")
    assert proxy_url() == "http://user:pa:ss:word@host:9000"


def test_a_url_is_left_alone(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", VENDOR_URL)
    assert proxy_url() == VENDOR_URL


def test_binolla_proxy_serves_as_the_fallback(monkeypatch):
    """One proxy line in scaralpha.env has to serve both processes."""
    monkeypatch.setenv("BINOLLA_AUTH_PROXY", VENDOR_LINE)
    assert proxy_url() == VENDOR_URL


def test_no_proxy_configured_is_not_an_error():
    assert proxy_url() is None
    assert configure_process_proxy() is None


def test_configuring_publishes_every_variable_the_transports_read(monkeypatch):
    """
    The Quotex library exposes no proxy setting at all — its curl_cffi session reads the
    environment — so these variables are the only channel that reaches it.
    """
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    configure_process_proxy()

    import os

    for name in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy"):
        assert os.environ[name] == VENDOR_URL


def test_localhost_bypasses_the_proxy(monkeypatch):
    """The .NET API reaches this service on 127.0.0.1; that must not be proxied."""
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    configure_process_proxy()

    import os

    assert "127.0.0.1" in os.environ["NO_PROXY"]
    assert "localhost" in os.environ["NO_PROXY"]


def test_an_operator_override_is_respected(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    monkeypatch.setenv("HTTPS_PROXY", "http://chosen.example:8080")

    configure_process_proxy()

    import os

    assert os.environ["HTTPS_PROXY"] == "http://chosen.example:8080"


def test_masking_never_leaks_credentials():
    masked = mask(VENDOR_URL)
    assert masked == "proxy.example.net:9000"
    assert "s3cr3t" not in masked
    assert "user-country-ae" not in masked
    assert mask(None) == "none"


def test_socks5_scheme_is_applied_to_a_bare_credential_line(monkeypatch):
    """
    Switching a deployment between HTTP and SOCKS5 should be one variable, not a rewritten
    credential line — vendors hand out `host:port:user:pass` with no scheme in it.
    """
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    monkeypatch.setenv("BROKER_PROXY_SCHEME", "socks5")

    # socks5h, not socks5: DNS resolves at the proxy, so the lookup and the traffic reach
    # the same CDN edge.
    assert proxy_url() == "socks5h://user-country-ae:s3cr3t@proxy.example.net:9000"


def test_an_explicit_url_keeps_its_own_scheme(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", "socks5://u:p@host:1080")
    monkeypatch.setenv("BROKER_PROXY_SCHEME", "http")
    assert proxy_url() == "socks5://u:p@host:1080"


def test_an_unknown_scheme_falls_back_to_http(monkeypatch):
    monkeypatch.setenv("BROKER_PROXY", VENDOR_LINE)
    monkeypatch.setenv("BROKER_PROXY_SCHEME", "carrier-pigeon")
    assert proxy_url() == VENDOR_URL
