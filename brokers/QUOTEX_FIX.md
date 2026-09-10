# Quotex connection repair

The gateway now authenticates the captured browser session before requesting account
data. It accepts login only after an actual balance response, preserving the selected
Demo/Real account. Failed login closes the client. Password-only connections to the
vendored client are rejected immediately; the existing .NET browser capture supplies
the SSID.

`app/quotex_protocol.py` receives live `instruments/list`, balance and quote events.
It assembles Socket.IO binary attachments and processes the transport queue once.
Assets are cached for 30 seconds and balances for 3 seconds, with one refresh at a time
per session. Missing responses raise an error; vendor mock assets and fabricated
balance defaults are not used. Quote ticks feed the existing shared candle store.
Historical candles still require prior streaming; this change does not supply historical
bars for a pair never observed by this gateway.

The transport sends WebSocket commands as text, serializes writes, bounds initial
upgrade waits, and treats an empty socket read as disconnection. Polling fallback uses
Engine.IO v3 length-prefixed POSTs, sends heartbeats, and stops on expired sessions.
The optional second TLS attempt is disabled by default to avoid extra login latency.
No changes to `vendor/` are required.

## Deployment

The supplied `quotex-fix.zip` contains a `brokers/` directory. Upload it to
`/home/web/backend/quotex-fix.zip`, then run:

```bash
cd /home/web/backend
tar -czf "quotex-before-fix-$(date +%Y%m%d-%H%M%S).tgz" \
  brokers/app brokers/tests brokers/ecosystem.config.cjs
unzip -o quotex-fix.zip
cd brokers
bash ./start-gateway-pm2.sh restart
curl -fsS http://127.0.0.1:8100/health
```

Restarting the gateway disconnects its current Quotex sessions. Sign in again through
the app and verify the selected account's balance against Quotex and the returned
instrument list. `/health` reports `quotex_live_transports` with actual connected
WebSocket/polling counts; the installed transport policy is shown separately.

`BROKER_QUOTEX_WS_TRANSPORT=0` selects vendor polling. `QUOTEX_WS_TRANSPORT` is the
legacy alias, used only when the first variable is absent. The default is `1`.
`QUOTEX_WS_PLAIN_FALLBACK=1` enables the extra websocket-client TLS attempt.
Both paths still require a valid captured session and working broker connectivity.

## Verification and limits

Run the gateway suite with:

```bash
.venv/bin/python -m pip install -e '.[dev]'
PYTHONDONTWRITEBYTECODE=1 .venv/bin/python -m pytest -q -p no:cacheprovider
```

Local tests cover successful/rejected authentication, selected account type, live zero
balances, missing responses, concurrent refreshes, binary event assembly, quote delivery,
sync send errors, pending-request cancellation, text frames, and expired polling SIDs.
The suite uses simulated broker messages. A production login, measured latency and
live trade execution have not been tested. Binolla's integration is unchanged.

The server's installed vendor source may differ from upstream. This compatibility
layer targets the ConnectionService surface with `_ws`, `_receive_messages`,
`_handle_message`, `_route_socketio_event` and `_pending_requests`.

## Protocol references

- [QuotexAPI ConnectionService](https://github.com/ChipaDevTeam/QuotexAPI/blob/main/QuotexAPI/services/connection.py): mixed synchronous/awaited transport sends.
- [QuotexAPI account service](https://github.com/ChipaDevTeam/QuotexAPI/blob/main/QuotexAPI/services/account.py): timeout defaults that the gateway now bypasses.
- [QuotexAPI instrument service](https://github.com/ChipaDevTeam/QuotexAPI/blob/main/QuotexAPI/services/instrument.py): placeholder asset implementation.
- [pyquotex wire event implementation](https://github.com/cleitonleonel/pyquotex/blob/master/pyquotex/api.py) and [asset schema](https://github.com/cleitonleonel/pyquotex/blob/master/pyquotex/_api/assets.py).
- [curl_cffi WebSocket source](https://curl-cffi.readthedocs.io/en/latest/_modules/curl_cffi/requests/websockets.html): synchronous recv signature and text frame API.
