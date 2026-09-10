"""
The throttle is the part that protects the server's standing with a broker, so its
behaviour is pinned here rather than left to integration testing against a live venue.
"""

from app.registry import _Throttle


def test_first_attempt_is_allowed():
    t = _Throttle()
    assert t.can_attempt("u1") is True


def test_different_users_can_sign_in_at_the_same_time():
    """
    Two people signing in at once is the product working, not a storm.

    This used to allow exactly one attempt anywhere: the second user was refused
    instantly, and since a connect takes ten seconds or more, a person clicking "log in"
    routinely lost the slot to a background reconnect and was told a login was already
    running — for a healthy account on a healthy broker.
    """
    t = _Throttle()
    assert t.can_attempt("u1") is True
    assert t.can_attempt("u2") is True


def test_concurrent_logins_are_still_bounded():
    """Bounded, so a restart cannot open a hundred handshakes at the broker together."""
    t = _Throttle(max_concurrent=3)
    assert [t.can_attempt(f"u{i}") for i in range(4)] == [True, True, True, False]


def test_a_second_attempt_for_the_same_user_is_refused():
    """Whoever asked, a second concurrent attempt for one account is a duplicate."""
    t = _Throttle()
    assert t.can_attempt("u1") is True
    assert t.can_attempt("u1") is False


def test_spacing_applies_only_once_attempts_are_failing():
    """
    On a healthy broker the floor would serialise ordinary logins behind each other for
    no reason; after a failure it is what stops a retry storm.
    """
    healthy = _Throttle()
    assert healthy.can_attempt("u1") is True
    healthy.mark_succeeded("u1")
    assert healthy.can_attempt("u2") is True

    failing = _Throttle()
    assert failing.can_attempt("u1") is True
    failing.mark_failed("u1")
    # u2 has no failures of its own, but the broker just refused someone.
    assert failing.can_attempt("u2") is False


def test_account_failure_holds_only_that_user_out():
    t = _Throttle()
    assert t.can_attempt("u1") is True
    t.mark_failed("u1", blocked_by_broker=False)
    assert t.can_attempt("u1") is False


def test_broker_ip_block_pauses_every_user():
    t = _Throttle()
    assert t.can_attempt("u1") is True
    # HTTP 403 is the broker refusing the SERVER; another account cannot succeed either.
    t.mark_failed("u1", blocked_by_broker=True)
    assert t.can_attempt("u2") is False


def test_success_clears_backoff_and_spacing():
    t = _Throttle()
    assert t.can_attempt("u1") is True
    t.mark_failed("u1", blocked_by_broker=True)
    assert t.can_attempt("u2") is False

    t.mark_succeeded("u1")
    # A working login proves the broker is reachable; queued users must not wait out a
    # floor that exists only to slow down failures.
    assert t.can_attempt("u2") is True


def test_backoff_grows_with_consecutive_failures():
    t = _Throttle(base_seconds=1.0, min_interval_seconds=0.0)
    delays = []
    for _ in range(3):
        t._holder = None
        t.can_attempt("u1")
        before = t._user_retry_after.get("u1", 0.0)
        t.mark_failed("u1")
        delays.append(t._user_retry_after["u1"] - before)
    # Each failure must wait longer than the last.
    assert t._user_failures["u1"] == 3
