#!/usr/bin/env python3
"""
Concurrent order placement: the failure that only appears when orders overlap.

Placing one option at a time worked for a thousand trades. Placing several in the same
second failed almost every time, with `[ORDER/CALL FAILED] TimeoutError:` and no message,
while the account stayed untouched. The cause was a test that had been correct while it
was impossible to be wrong: after a silent interval, `place_order` asked "did the balance
move, or did any s_orders frame arrive?" to decide whether its own order had opened. With
one order in flight that question has only one possible subject. With six, the first
order's acknowledgement answers it for all six, so five of them conclude they opened,
stop resending, and wait out an acknowledgement addressed to someone else.

These tests pin the behaviour that distinction requires:
  * one order's ack must not satisfy another order's open-check;
  * an ack that names no order must not be handed to an arbitrary waiter;
  * several orders placed at once must each resolve to their own deal.
"""
from __future__ import annotations

import asyncio
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from app.quotex_protocol import QuotexLiveData  # noqa: E402

#: Futures need a loop to belong to; nothing here ever runs it.
LOOP = asyncio.new_event_loop()


def future():
    return LOOP.create_future()


def make_client():
    """A client with no socket, enough to exercise the order-correlation logic."""
    c = QuotexLiveData.__new__(QuotexLiveData)
    c._pending_orders = []
    c._recent_events = __import__("collections").deque(maxlen=200)
    c.balance_at = 1000.0
    c._asset_index = {}
    c.closed_orders = {}
    return c


def test_other_orders_ack_does_not_satisfy_this_one():
    c = make_client()
    sent_at = time.time() - 0.1
    # Two orders in flight; only EURUSD_otc was acknowledged.
    f1, f2 = future(), future()
    c._pending_orders = [(111, "EURUSD_otc", f1), (222, "GBPUSD_otc", f2)]
    c._recent_events.append((time.time(), "s_orders/open",
                             "{'requestId': 111, 'asset': 'EURUSD_otc'}"))

    assert c._order_opened(111, "EURUSD_otc", 1000.0, sent_at) is True, \
        "the acknowledged order must see its own ack"
    assert c._order_opened(222, "GBPUSD_otc", 1000.0, sent_at) is False, \
        "the unacknowledged order must NOT be satisfied by the other's ack"
    print("ok  one order's ack does not satisfy another")


def test_balance_move_alone_counts_only_when_nothing_else_is_pending():
    c = make_client()
    sent_at = time.time() - 0.1
    f1 = future()

    # Sole order, balance moved, no identifying frame: the old global test is sound.
    c._pending_orders = [(111, "EURUSD_otc", f1)]
    c.balance_at = 998.0
    assert c._order_opened(111, "EURUSD_otc", 1000.0, sent_at) is True

    # Same evidence, but a second order could equally have moved the balance.
    c._pending_orders = [(111, "EURUSD_otc", f1), (222, "GBPUSD_otc", future())]
    assert c._order_opened(111, "EURUSD_otc", 1000.0, sent_at) is False, \
        "a bare balance move is ambiguous while another order is in flight"
    print("ok  a bare balance move counts only when unambiguous")


def test_anonymous_ack_is_not_handed_to_an_arbitrary_waiter():
    c = make_client()
    f1, f2 = future(), future()
    c._pending_orders = [(111, "EURUSD_otc", f1), (222, "GBPUSD_otc", f2)]

    # A definitive ack carrying neither a request id nor an asset.
    c._resolve_pending_order({"id": 9001}, "s_orders/open")
    assert not f1.done() and not f2.done(), \
        "an ack naming no order must not be guessed onto a waiter"
    assert len(c._pending_orders) == 2

    # With a single waiter the same frame is unambiguous and must resolve it.
    c._pending_orders = [(111, "EURUSD_otc", f1)]
    c._resolve_pending_order({"id": 9001}, "s_orders/open")
    assert f1.done() and f1.result()["id"] == 9001
    print("ok  an anonymous ack is matched only when unambiguous")


def test_concurrent_orders_each_get_their_own_deal():
    c = make_client()
    futures = {}
    for rid, asset in ((101, "EURUSD_otc"), (102, "GBPUSD_otc"), (103, "AUDCAD_otc")):
        f = future()
        futures[rid] = f
        c._pending_orders.append((rid, asset, f))

    # Acks arrive out of order, as they do on the wire.
    for rid, deal in ((103, 9003), (101, 9001), (102, 9002)):
        c._resolve_pending_order({"requestId": rid, "id": deal, "asset": "x"},
                                 "s_orders/open")

    assert all(f.done() for f in futures.values()), "every order must resolve"
    assert futures[101].result()["id"] == 9001
    assert futures[102].result()["id"] == 9002
    assert futures[103].result()["id"] == 9003, "each order must get its OWN deal id"
    assert c._pending_orders == []
    print("ok  three concurrent orders each resolve to their own deal")


def main():
    for fn in (test_other_orders_ack_does_not_satisfy_this_one,
               test_balance_move_alone_counts_only_when_nothing_else_is_pending,
               test_anonymous_ack_is_not_handed_to_an_arbitrary_waiter,
               test_concurrent_orders_each_get_their_own_deal):
        fn()
    print("\nall concurrent-order tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
