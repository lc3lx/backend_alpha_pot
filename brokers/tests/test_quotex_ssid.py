"""
Unwrapping the session captured from a browser.

The capture watches the site's own socket and keeps the authorisation it sees, which is a
whole Socket.IO frame. The Quotex library builds that frame itself, so what it wants is
the bare session value inside it. Getting this wrong is not a loud failure: the frame goes
out double-wrapped, the broker ignores it, and the session dies later with "Session ID
unknown" — the exact symptom this whole path exists to remove.
"""

from __future__ import annotations

from app.brokers.quotex import _normalize_ssid

FRAME = '42["authorization",{"session":"abc123sessionvalue","isDemo":0,"tournamentId":0}]'


def test_a_captured_frame_is_reduced_to_its_session_value():
    assert _normalize_ssid(FRAME) == "abc123sessionvalue"


def test_a_bare_token_passes_through_untouched():
    assert _normalize_ssid("abc123sessionvalue") == "abc123sessionvalue"


def test_surrounding_whitespace_is_not_part_of_the_session():
    # The value crosses a process boundary as text; a stray newline would be sent verbatim.
    assert _normalize_ssid("  abc123sessionvalue \n") == "abc123sessionvalue"


def test_a_binolla_shaped_frame_still_yields_its_token():
    # Not the expected input, but a mislinked account must not silently send a JSON blob
    # as if it were a session.
    frame = '42["authorization",{"isDemo":true,"token":"binolla-token-value"}]'
    assert _normalize_ssid(frame) == "binolla-token-value"


def test_isdemo_from_the_capture_is_discarded():
    # The account to trade on is the caller's choice, not whatever balance the browser
    # happened to land on. Carrying isDemo through is how a demo request places a real
    # trade, so the value must not survive the unwrap.
    demo_frame = '42["authorization",{"session":"s","isDemo":1,"tournamentId":0}]'
    assert _normalize_ssid(demo_frame) == "s"


def test_nothing_captured_stays_nothing():
    # Distinct from an empty string: the caller branches on None to fall back to
    # email/password, and "" would look like a session it could use.
    assert _normalize_ssid(None) is None
    assert _normalize_ssid("   ") is None


def test_an_unparseable_frame_is_kept_rather_than_dropped():
    # A truncated capture is still worth attempting; discarding it would turn a maybe into
    # a guaranteed failure with no explanation.
    assert _normalize_ssid('42["authorization",{"session":') == '42["authorization",{"session":'
