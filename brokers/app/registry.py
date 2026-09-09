"""
One live session per (user, broker), plus the throttles that keep a refusing broker from
being hammered.

The throttling here is a direct port of what the .NET side learned the hard way: a broker
that refuses logins will keep refusing, and retrying at full speed turns a temporary block
into a lasting IP ban. Two levels are needed because the two failure modes differ:

  * per-user  — bad credentials for one account
  * global    — the broker refused the SERVER (403 / geo / WAF), which is not per-account,
                so retrying on behalf of somebody else cannot succeed and only deepens it
"""

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field

from app.brokers.base import BrokerSession


@dataclass
class _Throttle:
    """Backoff state for one broker's login attempts."""

    #: Seconds for the first failure; doubles per consecutive failure.
    base_seconds: float = 30.0
    max_seconds: float = 900.0
    #: Floor between attempts regardless of how many users want one.
    min_interval_seconds: float = 20.0

    _user_retry_after: dict[str, float] = field(default_factory=dict)
    _user_failures: dict[str, int] = field(default_factory=dict)
    _in_flight: set[str] = field(default_factory=set)
    _holder: str | None = None
    _last_started: float = 0.0
    _global_retry_after: float = 0.0
    _global_failures: int = 0

    def can_attempt(self, user_id: str) -> bool:
        now = time.monotonic()
        if now < self._user_retry_after.get(user_id, 0.0):
            return False
        if now < self._global_retry_after:
            return False
        if self._holder is not None:
            return False
        if now - self._last_started < self.min_interval_seconds:
            return False

        self._holder = user_id
        self._in_flight.add(user_id)
        self._last_started = now
        return True

    def mark_failed(self, user_id: str, *, blocked_by_broker: bool = False) -> None:
        self._release(user_id)

        failures = self._user_failures.get(user_id, 0) + 1
        self._user_failures[user_id] = failures
        delay = min(self.base_seconds * (2 ** min(failures - 1, 6)), self.max_seconds)
        self._user_retry_after[user_id] = time.monotonic() + delay

        if not blocked_by_broker:
            return

        self._global_failures = min(self._global_failures + 1, 8)
        global_delay = min(60.0 * (2 ** min(self._global_failures - 1, 4)), self.max_seconds)
        self._global_retry_after = time.monotonic() + global_delay

    def mark_succeeded(self, user_id: str) -> None:
        self._release(user_id)
        self._user_failures.pop(user_id, None)
        self._user_retry_after.pop(user_id, None)
        self._global_failures = 0
        self._global_retry_after = 0.0
        # The spacing floor throttles FAILED attempts. A success proves the broker is
        # reachable, so it must not hold other users back.
        self._last_started = 0.0

    def _release(self, user_id: str) -> None:
        self._in_flight.discard(user_id)
        if self._holder == user_id:
            self._holder = None


class SessionRegistry:
    """Holds live sessions and serialises connect attempts per broker."""

    def __init__(self, max_sessions: int = 500) -> None:
        self._sessions: dict[tuple[str, str], BrokerSession] = {}
        self._throttles: dict[str, _Throttle] = {}
        self._lock = asyncio.Lock()
        self._max_sessions = max_sessions

    def throttle(self, broker: str) -> _Throttle:
        return self._throttles.setdefault(broker, _Throttle())

    def get(self, user_id: str, broker: str) -> BrokerSession | None:
        return self._sessions.get((user_id, broker))

    async def put(self, session: BrokerSession) -> None:
        async with self._lock:
            if len(self._sessions) >= self._max_sessions:
                raise RuntimeError(f"Max concurrent sessions reached ({self._max_sessions}).")
            self._sessions[(session.user_id, session.broker)] = session

    async def remove(self, user_id: str, broker: str) -> None:
        async with self._lock:
            session = self._sessions.pop((user_id, broker), None)
        if session is not None:
            try:
                await session.disconnect()
            except Exception:
                # Best effort: the entry is gone either way, and a failing disconnect on a
                # already-dead socket must not block the caller.
                pass

    def all_sessions(self) -> list[BrokerSession]:
        return list(self._sessions.values())

    @property
    def count(self) -> int:
        return len(self._sessions)
