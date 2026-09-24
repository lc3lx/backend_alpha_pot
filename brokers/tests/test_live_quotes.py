from app.market_data import MarketData


def test_live_quotes_follow_ticks():
    store = MarketData("quotex")
    store.apply_quote("EURUSD", {"time": 1_000.0, "price": 1.0851})
    store.apply_quote("GBPUSD_otc", {"time": 1_001.0, "price": 1.2702})
    prices = {asset: price for asset, _ts, price in store.live_quotes()}
    assert prices["EURUSD"] == 1.0851
    assert prices["GBPUSD_otc"] == 1.2702
