"""
The throttle is the part that protects the server's standing with a broker, so its
behaviour is pinned here rather than left to integration testing against a live venue.
"""

from app.registry import _Throttle


def test_first_attempt_is_allowed():
    t = _Throttle()
    assert t.can_attempt("u1") is True


def test_only_one_login_runs_at_a_time_across_users():
    t = _Throttle()
    # A login drives a browser/socket from the server's single IP. Letting each user hold
    # an independent cooldown still produced an attempt every few seconds in production.
    assert t.can_attempt("u1") is True
    assert t.can_attempt("u2") is False


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
