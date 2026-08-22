using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Binolla.Protocol;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class RsiSignalAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaSessionManager _sessions;
    private readonly IBotAccessService _botAccess;
    private readonly IRsiSignalService _signalService;
    private readonly IEmaRsiSignalService _emaSignalService;
    private readonly MarketAnalysisCache _analysisCache;
    private readonly EmaRsiTradeTracker _emaTracker;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly TradeAppService _trades;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<RsiSignalAppService> _logger;

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IEmaRsiSignalService emaSignalService,
        MarketAnalysisCache analysisCache,
        EmaRsiTradeTracker emaTracker,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        TradeAppService trades,
        IBotRuntimeService botRuntime,
        ITradeRepository tradeRepository,
        ILogger<RsiSignalAppService> logger)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
        _emaSignalService = emaSignalService;
        _analysisCache = analysisCache;
        _emaTracker = emaTracker;
        _restorer = restorer;
        _demo = demo;
        _trades = trades;
        _botRuntime = botRuntime;
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    public Task<StrategySignal> TryAutoExecuteAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        CancellationToken ct) =>
        ExecuteIfRequestedAsync(signal, options, autoExecute: true, ct);

    public async Task<StrategySignal> GetSignalAsync(
        string asset,
        int periodSeconds,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct,
        bool skipMarketAccess = false,
        string strategyId = "rsi")
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ApiException(ApiErrorCodes.ValidationError, "asset is required.");
        if (periodSeconds != 60)
            throw new ApiException(ApiErrorCodes.ValidationError, "RSI Smart Backtest only supports the 1-minute timeframe.");

        if (!skipMarketAccess && await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
        {
            // Demo payloads are display-only — never auto-place from synthetic RSI.
            return _demo.BuildRsiSignal(asset, periodSeconds);
        }

        if (!skipMarketAccess)
        {
            var access = await _botAccess.CheckAsync(_currentUser.UserId, ct);
            AccountAppService.EnsureConnectedForMarket(access);
        }

        // While a live trade is open for this user: do not analyze / place.
        if (await IsAnalysisPausedAsync(ct))
        {
            return SoftNone(asset.Trim(), periodSeconds) with
            {
                AutomationError = "OPEN_TRADE_EXISTS"
            };
        }

        var symbol = asset.Trim();
        var wirePeriod = BinollaMarketPeriods.NormalizeHistoryPeriod(periodSeconds);
        var client = await EnsureLiveClientAsync(ct);
        if (client is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "RsiSignalAppService.GetSignalAsync",
                "rsi_soft_not_live",
                new { symbol, wirePeriod });
            // #endregion
            return SoftNone(symbol, wirePeriod);
        }

        if (await IsAnalysisPausedAsync(ct))
        {
            return SoftNone(symbol, wirePeriod) with { AutomationError = "OPEN_TRADE_EXISTS" };
        }

        // GetHistoryAsync already SubscribePair on a cache miss. Only warm when
        // candles are in RAM but the live quote went stale (refresh quotes, no 2nd history dump).
        const int maxQuoteAgeSeconds = 15;
        var quoteFresh = client.TryGetCachedQuote(symbol, out var cachedQuote)
            && cachedQuote is not null
            && (DateTimeOffset.UtcNow - cachedQuote.ReceivedAt).TotalSeconds <= maxQuoteAgeSeconds;
        if (client.HasFreshHistory(symbol, wirePeriod) && !quoteFresh)
            client.EnsureMarketDataWarm(symbol, wirePeriod);
        try
        {
            if (await IsAnalysisPausedAsync(ct))
            {
                return SoftNone(symbol, wirePeriod) with { AutomationError = "OPEN_TRADE_EXISTS" };
            }

            if (!quoteFresh)
                quoteFresh = await WaitForFreshQuoteAsync(client, symbol, maxQuoteAgeSeconds, ct);

            var now = DateTimeOffset.UtcNow;
            var period = TimeSpan.FromSeconds(wirePeriod);

            // One analysis per pair per closed bar, shared by every account: the first
            // caller fetches and computes, the rest reuse the identical closed series.
            // Two accounts on the same pair therefore always see the same signal, and
            // 100 users cost the same history traffic as one.
            var analysis = await GetSharedAnalysisAsync(client, symbol, wirePeriod, options, now, ct);
            if (analysis is null)
                return SoftNone(symbol, wirePeriod) with { AutomationError = "INSUFFICIENT_HISTORY" };

            var candles = analysis.ClosedCandles.ToList();
            if (candles.Count == 0)
                return SoftNone(symbol, wirePeriod);

            // Pine's checkpoint + win/loss counters, advanced on the closed series.
            _emaTracker.Resolve(_currentUser.UserId, symbol, analysis.ClosedCandles, wirePeriod);

            if (await IsAnalysisPausedAsync(ct))
            {
                return SoftNone(symbol, wirePeriod) with { AutomationError = "OPEN_TRADE_EXISTS" };
            }

            _ = ApplyLiveQuoteToCandles(client, symbol, candles, wirePeriod, now);

            var lastClosed = candles.LastOrDefault(c => c.Timestamp + period <= now);
            if (lastClosed is { } closedBar &&
                (now - (closedBar.Timestamp + period)).TotalSeconds > wirePeriod)
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-LIVE1",
                    "RsiSignalAppService.GetSignalAsync",
                    "rsi_closed_bar_stale",
                    new
                    {
                        symbol,
                        lastClosedStart = closedBar.Timestamp.ToUnixTimeSeconds(),
                        ageSec = Math.Round((now - (closedBar.Timestamp + period)).TotalSeconds, 1),
                        candleCount = candles.Count
                    },
                    runId: "live-rsi");
                // #endregion
                return SoftNone(symbol, wirePeriod);
            }

            // When regime is on it already establishes the trend from the 1m series, so
            // the Pine 15m EMA200 filter is redundant — and dropping it removes a second
            // subscription per pair plus the TREND_UNKNOWN failure mode entirely.
            var emaOptions = EmaRsiOptions.FromBotDurationSeconds(options.ExpiryCandles * 60)
                with { UseTrendFilter = EmaTrendFilterEnabled && !StrategyGate.RegimeEnabled };

            // Smart mode hands engine choice to the regime; an explicit choice is kept.
            var engineId = RegimeRouting.IsSmart(strategyId)
                ? RegimeRouting.EngineFor(analysis.Regime?.Regime ?? MarketRegime.Unclear)
                : strategyId;
            if (engineId is null)
            {
                return SoftNone(symbol, wirePeriod) with
                {
                    AutomationError = RegimeRouting.RejectUnclear,
                    Regime = analysis.Regime?.Regime ?? MarketRegime.Unclear,
                    RegimeReason = analysis.Regime?.Reason,
                    RegimeApplied = analysis.Regime is not null,
                };
            }

            var signal = IsEma(engineId)
                ? await _emaSignalService.GetSignalAsync(
                    userId: _currentUser.UserId,
                    asset: symbol,
                    candles: candles,
                    // Pine's htfTF series. EMA200 on 15m needs ~3000 minutes of data, far
                    // more than the 1m cache holds, so this is a real 15m subscription —
                    // shared and refreshed only once per pair per 15m bar.
                    trendCloses: await GetTrendClosesAsync(client, symbol, emaOptions, now, ct),
                    options: emaOptions,
                    now: now,
                    ct: ct)
                : await _signalService.GetSignalAsync(
                    userId: _currentUser.UserId,
                    asset: symbol,
                    candles: candles,
                    options: options,
                    now: now,
                    ct: ct);

            signal = signal with
            {
                Regime = analysis.Regime?.Regime ?? MarketRegime.Unclear,
                RegimeReason = analysis.Regime?.Reason,
                RegimeApplied = analysis.Regime is not null,
                RelativeVolume = analysis.Regime?.RelativeVolume,
                VolumeOk = analysis.Regime?.VolumeOk ?? true,
            };

            return await ExecuteIfRequestedAsync(signal, options, autoExecute, ct, analysis.Regime);
        }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (BinollaTimeoutException)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.LoginTrace.Write(
                "H131",
                "RsiSignalAppService.GetSignalAsync",
                "rsi_soft_timeout",
                new { symbol, wirePeriod });
            // #endregion
            return SoftNone(symbol, wirePeriod);
        }
        catch (OperationCanceledException)
        {
            return SoftNone(symbol, wirePeriod);
        }
        catch (BinollaConnectionException)
        {
            return SoftNone(symbol, wirePeriod);
        }
        catch (ApiException ex) when (ex.Code == ApiErrorCodes.ValidationError)
        {
            // Insufficient candles — soft None, not 400/500 spam.
            return SoftNone(symbol, wirePeriod);
        }
        catch (ApiException) { throw; }
        catch (Exception)
        {
            return SoftNone(symbol, wirePeriod);
        }
    }

    /// <summary>
    /// Pine's <c>useTrend</c>. Kept operator-tunable (appsettings
    /// <c>Strategy:EmaUseTrendFilter</c>) because it depends on the broker actually
    /// serving ~200 bars of 15m history; without that the trend is Unknown and the
    /// strategy correctly refuses to trade.
    /// </summary>
    public static bool EmaTrendFilterEnabled { get; set; } = true;

    /// <summary>
    /// Closes of the trend timeframe (Pine htfTF, default 15m), shared across accounts
    /// and refetched only once per pair per trend bar. Returns an empty list when the
    /// broker cannot supply enough history — the engine then reports TREND_UNKNOWN
    /// rather than assuming the trend agrees.
    /// </summary>
    private async Task<IReadOnlyList<decimal>> GetTrendClosesAsync(
        IBinollaClient client,
        string symbol,
        EmaRsiOptions emaOptions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!emaOptions.UseTrendFilter)
            return Array.Empty<decimal>();

        var tf = BinollaMarketPeriods.NormalizeHistoryPeriod(emaOptions.TrendTimeframeSeconds);

        try
        {
            var trend = await _analysisCache.GetOrAddAsync(symbol, tf, now, async _ =>
            {
                var history = await client.GetHistoryAsync(symbol, tf, CancellationToken.None);
                var period = TimeSpan.FromSeconds(tf);

                List<CandlestickData> raw;
                lock (history.Candles)
                    raw = history.Candles.ToList();

                var bars = MinuteBars.Normalize(raw, tf)
                    .Select(c =>
                    {
                        var start = DateTimeOffset.FromUnixTimeSeconds(
                            MinuteBars.BucketStartUnix(c.Timestamp, tf));
                        return ToCandle(c, start, period);
                    })
                    .ToList();

                var prepared = CandleSeries.Prepare(bars, tf, now);
                if (prepared.Count < IndicatorWarmup.ForEma(emaOptions.TrendEmaLength))
                {
                    // #region agent log
                    ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                        "H-TREND",
                        "RsiSignalAppService.GetTrendClosesAsync",
                        "trend_history_too_short",
                        new
                        {
                            symbol,
                            timeframe = tf,
                            closed = prepared.Count,
                            required = IndicatorWarmup.ForEma(emaOptions.TrendEmaLength)
                        },
                        runId: "ema-trend");
                    // #endregion
                    return null;
                }

                return new MarketAnalysis(
                    Asset: symbol,
                    TimeframeSeconds: tf,
                    ClosedBarTime: prepared.Last.Timestamp + period,
                    ClosedCandles: prepared.Closed,
                    Closes: prepared.Closes,
                    Rsi: 0m,
                    CallBacktest: null,
                    PutBacktest: null);
            }, ct);

            return trend?.Closes ?? Array.Empty<decimal>();
        }
        catch (Exception)
        {
            // Trend data is a filter, not a data source — a failure means "cannot
            // confirm", which blocks the entry rather than allowing an unfiltered one.
            return Array.Empty<decimal>();
        }
    }

    /// <summary>
    /// Full bar for the strategy layer: range and volume are carried through so ATR/ADX
    /// and the volume filter have something to work with. Volume stays null whenever
    /// Binolla did not send it — the filter treats that as "not available", not as zero.
    /// </summary>
    private static RsiCandle ToCandle(CandlestickData c, DateTimeOffset start, TimeSpan period) =>
        new(
            Timestamp: start,
            Close: (decimal)c.Close,
            EndTimestamp: start + period,
            High: (decimal)c.High,
            Low: (decimal)c.Low,
            Open: (decimal)c.Open,
            Volume: c.Volume is double v ? (decimal)v : null);

    internal static bool IsEma(string? strategyId) =>
        string.Equals(strategyId?.Trim(), "ema", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds (or reuses) the account-independent analysis for one pair and closed bar.
    ///
    /// Returns null — and therefore caches nothing — when the series is not good enough
    /// to trust: too little warmup for Wilder RSI, or a gap in the minutes. Emitting no
    /// signal is correct there; emitting an RSI the broker chart disagrees with is not.
    /// </summary>
    private Task<MarketAnalysis?> GetSharedAnalysisAsync(
        IBinollaClient client,
        string symbol,
        int wirePeriod,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct) =>
        _analysisCache.GetOrAddAsync(symbol, wirePeriod, now, async _ =>
        {
            var history = await client.GetHistoryAsync(symbol, wirePeriod, CancellationToken.None);
            var period = TimeSpan.FromSeconds(wirePeriod);

            List<CandlestickData> raw;
            lock (history.Candles)
                raw = history.Candles.ToList();

            var candles = MinuteBars.Normalize(raw, wirePeriod)
                .Select(c =>
                {
                    var start = DateTimeOffset.FromUnixTimeSeconds(
                        MinuteBars.BucketStartUnix(c.Timestamp, wirePeriod));
                    // Period end, never Binolla's last-tick EndTimestamp.
                    return ToCandle(c, start, period);
                })
                .ToList();

            var prepared = CandleSeries.Prepare(candles, wirePeriod, now);
            var warmup = Math.Max(
                IndicatorWarmup.ForRsi(options.Period),
                options.Period + options.ExpiryCandles + 2);
            if (prepared.Count < warmup)
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-WARMUP",
                    "RsiSignalAppService.GetSharedAnalysisAsync",
                    "insufficient_warmup",
                    new
                    {
                        symbol,
                        closed = prepared.Count,
                        rawClosed = prepared.RawCount,
                        gapsDropped = prepared.GapsDropped,
                        required = warmup
                    },
                    runId: "rsi-parity");
                // #endregion
                return null;
            }

            var closes = prepared.Closes;
            var rsi = Math.Round(
                Indicators.Rsi(closes, options.Period)!.Value, 2, MidpointRounding.AwayFromZero);
            var (call, put) = RsiZoneBacktest.Evaluate(closes, options);
            // Same closed series, so every account sees the same regime for this bar.
            var regime = MarketRegimeClassifier.Analyze(prepared.Closed, MarketRegimeOptions.Default);

            return new MarketAnalysis(
                Asset: symbol,
                TimeframeSeconds: wirePeriod,
                ClosedBarTime: prepared.Last.Timestamp + period,
                ClosedCandles: prepared.Closed,
                Closes: closes,
                Rsi: rsi,
                CallBacktest: call,
                PutBacktest: put,
                Regime: regime);
        }, ct);

    private static StrategySignal SoftNone(string symbol, int periodSeconds) =>
        new(
            StrategyId: "rsi",
            Asset: symbol,
            Signal: "None",
            Rsi: 0m,
            CandleTime: DateTimeOffset.UtcNow,
            Timeframe: periodSeconds.ToString());

    private async Task<StrategySignal> ExecuteIfRequestedAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct,
        RegimeSnapshot? regime = null)
    {
        var userId = _currentUser.UserId;
        var bot = _botRuntime.Get(userId);
        // Home polls without autoExecute still place when Running — but only after full gate.
        if (bot.State != BotRunState.Running)
            return signal;

        // Only the strategy the user selected may trade. The Mini App polls the RSI
        // endpoint directly, so without this a bot configured for EMA could still have
        // an RSI setup placed underneath it.
        // A smart bot legitimately runs either engine, so the mismatch check only
        // applies when the user pinned one.
        if (!RegimeRouting.IsSmart(bot.StrategyId) &&
            IsEma(bot.StrategyId) != IsEma(signal.StrategyId))
            return signal with { AutomationError = "STRATEGY_MISMATCH", Signal = "None" };

        var nowGate = DateTimeOffset.UtcNow;
        if (!StrategyGate.TryValidateForTrade(signal, nowGate, bot.StrategyId, regime, out var rejectCode))
        {
            if (rejectCode is null or "NO_SIGNAL")
                return signal;
            return signal with { AutomationError = rejectCode, Signal = "None" };
        }

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE",
            "RsiSignalAppService.ExecuteIfRequestedAsync",
            "execute_live_setup",
            new
            {
                asset = signal.Asset,
                signal = signal.Signal,
                liveRsi = signal.LiveRsi,
                closedRsi = signal.Rsi,
                successRate = signal.Backtest?.SuccessRate,
                totalSignals = signal.Backtest?.TotalSignals,
                ageSec = Math.Round((nowGate - signal.CandleTime).TotalSeconds, 2),
                botState = bot.State.ToString(),
                autoExecute
            },
            runId: "missed-entry");
        // #endregion

        var selected = bot.ResolvedAssets;
        if (selected.Count > 0 && !BotAssetList.Contains(selected, signal.Asset))
            return signal with { AutomationError = "ASSET_NOT_SELECTED" };

        var dailyLimit = await GetReachedDailyLimitAsync(bot, ct);
        if (dailyLimit is not null)
        {
            _botRuntime.Stop(userId, dailyLimit);
            return signal with { AutomationError = dailyLimit };
        }

        // One live trade at a time — expired/stuck Running rows must not freeze the bot.
        var now = DateTimeOffset.UtcNow;
        var runningRows = await _tradeRepository.ListByUserAsync(
            userId, take: 20, status: TradeStatus.Running, ct: ct);
        var pendingRows = await _tradeRepository.ListByUserAsync(
            userId, take: 10, status: TradeStatus.Pending, ct: ct);
        var blocking = runningRows.Concat(pendingRows).Where(t => OpenTradeGate.IsBlocking(t, now)).ToList();
        if (blocking.Count > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "RsiSignalAppService.ExecuteIfRequestedAsync",
                "skip_open_trade_exists",
                new
                {
                    blocking = blocking.Count,
                    staleRunning = runningRows.Count - runningRows.Count(t => OpenTradeGate.IsBlocking(t, now)),
                    pending = pendingRows.Count,
                    asset = signal.Asset,
                    oldestAgeSec = blocking.Count == 0
                        ? 0
                        : Math.Round((now - blocking.Min(t => t.CreatedAt)).TotalSeconds, 0)
                },
                runId: "stuck-running");
            // #endregion
            return signal with { AutomationError = "OPEN_TRADE_EXISTS" };
        }

        if (runningRows.Count + pendingRows.Count > 0)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-STUCK1",
                "RsiSignalAppService.ExecuteIfRequestedAsync",
                "proceed_despite_stale_open",
                new
                {
                    staleRunning = runningRows.Count,
                    pending = pendingRows.Count,
                    asset = signal.Asset
                },
                runId: "stuck-running");
            // #endregion
        }

        var gate = await _autoTradeGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1))
            .WaitAsync(0, ct);
        if (!gate)
            return signal with { AutomationError = "AUTO_TRADE_BUSY" };

        try
        {
            runningRows = await _tradeRepository.ListByUserAsync(
                userId, take: 20, status: TradeStatus.Running, ct: ct);
            pendingRows = await _tradeRepository.ListByUserAsync(
                userId, take: 10, status: TradeStatus.Pending, ct: ct);
            if (runningRows.Concat(pendingRows).Any(t => OpenTradeGate.IsBlocking(t, DateTimeOffset.UtcNow)))
                return signal with { AutomationError = "OPEN_TRADE_EXISTS" };

            // Trade duration must match the backtest expiry window (3–5 minutes on 1m).
            var expiryCandles = signal.Backtest.ExpiryCandles;
            if (expiryCandles is < 3 or > 5)
                expiryCandles = options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5;
            var durationSeconds = expiryCandles * 60;
            OpenTradeGate.MarkUserHeld(userId, durationSeconds);

            var strategyKey = IsEma(signal.StrategyId) ? "ema" : "rsi";
            var key = $"bot:{strategyKey}:{signal.Asset.Trim().ToUpperInvariant()}:{signal.CandleTime.ToUnixTimeSeconds()}:{signal.Signal}";
            try
            {
                var putOk = signal.LiveRsi is decimal lr && lr >= RsiEntryLevels.PutMin;
                var callOk = signal.LiveRsi is decimal lc && lc <= RsiEntryLevels.CallMax;
                var violation = (signal.Signal == "Put" && !putOk) || (signal.Signal == "Call" && !callOk);
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-C",
                    "RsiSignalAppService.ExecuteIfRequestedAsync",
                    "place_rsi_snapshot",
                    new
                    {
                        asset = signal.Asset,
                        signal = signal.Signal,
                        liveRsi = signal.LiveRsi,
                        closedRsi = signal.Rsi,
                        rsiEqClosed = signal.LiveRsi == signal.Rsi,
                        putOk,
                        callOk,
                        violation,
                        successRate = signal.Backtest?.SuccessRate
                    },
                    runId: "rsi-zone");
                // #endregion
                var trade = await _trades.PlaceTradeAsync(new PlaceTradeRequest(
                    Asset: signal.Asset,
                    Direction: signal.Signal.ToUpperInvariant(),
                    Amount: bot.Amount,
                    DurationSeconds: durationSeconds,
                    StrategyId: strategyKey), key, ct);

                if (IsEma(signal.StrategyId))
                {
                    _emaSignalService.MarkSignalEmitted(
                        userId, signal.Asset, options.TimeframeSeconds, signal.CandleTime);

                    // Pine scores from the close of the signal bar, which is where we entered.
                    var entryClose = _analysisCache
                        .TryGet(signal.Asset, options.TimeframeSeconds, DateTimeOffset.UtcNow)?
                        .ClosedCandles[^1].Close;
                    if (entryClose is decimal price)
                    {
                        _emaTracker.RecordEntry(
                            userId,
                            signal.Asset,
                            signal.Signal,
                            price,
                            signal.CandleTime,
                            checkpointBars: EmaRsiOptions.Default.CheckpointBars,
                            durationBars: expiryCandles,
                            timeframeSeconds: options.TimeframeSeconds);
                    }
                }
                else
                {
                    _signalService.MarkSignalEmitted(
                        userId, signal.Asset, options.TimeframeSeconds, signal.CandleTime);
                }

                // #region agent log
                var tradeIdShort = trade.Id.Length >= 8 ? trade.Id[..8] : trade.Id;
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "OT1",
                    "RsiSignalAppService.ExecuteIfRequestedAsync",
                    "placed_one_trade",
                    new
                    {
                        tradeId = tradeIdShort,
                        asset = signal.Asset,
                        signal = signal.Signal,
                        durationSeconds,
                        successRate = signal.Backtest.SuccessRate
                    });
                // #endregion
                _logger.LogInformation(
                    "Placed RSI {Direction} {Asset} trade={TradeId} duration={Duration}s rate={Rate:F0}% liveRsi={LiveRsi} closedRsi={ClosedRsi}",
                    signal.Signal,
                    signal.Asset,
                    trade.Id,
                    durationSeconds,
                    signal.Backtest.SuccessRate,
                    signal.LiveRsi,
                    signal.Rsi);
                return signal with { AutomatedTradeId = trade.Id };
            }
            catch (ApiException ex)
            {
                OpenTradeGate.ReleaseUser(userId);
                _logger.LogWarning(
                    "RSI place skipped {Asset} {Direction} code={Code}",
                    signal.Asset,
                    signal.Signal,
                    ex.Code);
                return signal with { AutomationError = ex.Code };
            }
        }
        finally
        {
            if (_autoTradeGates.TryGetValue(userId, out var sem))
                sem.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _autoTradeGates = new();

    private async Task<string?> GetReachedDailyLimitAsync(BotRuntimeConfig bot, CancellationToken ct)
    {
        if ((!bot.AutoStopAtProfit || bot.DailyProfitTarget <= 0m) &&
            (!bot.AutoStopAtLoss || bot.DailyLossLimit <= 0m))
            return null;

        // Session baseline: after user presses Start again, prior PnL does not count.
        var startOfUtcDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var from = bot.PnlSessionStartedAt ?? startOfUtcDay;

        var trades = await _tradeRepository.ListByUserAsync(_currentUser.UserId, take: 1000, ct: ct);
        var pnl = trades
            .Where(trade =>
                trade.UpdatedAt >= from &&
                trade.Status is TradeStatus.Profit or TradeStatus.Loss)
            .Sum(trade => trade.Pnl ?? 0m);

        if (bot.AutoStopAtProfit && bot.DailyProfitTarget > 0m && pnl >= bot.DailyProfitTarget)
            return "DAILY_PROFIT_TARGET_REACHED";
        if (bot.AutoStopAtLoss && bot.DailyLossLimit > 0m && pnl <= -bot.DailyLossLimit)
            return "DAILY_LOSS_LIMIT_REACHED";

        return null;
    }

    private Task<IBinollaClient?> EnsureLiveClientAsync(CancellationToken ct)
    {
        var client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return Task.FromResult<IBinollaClient?>(client);
        }

        // Signal polling is latency-sensitive; reconnect without blocking this request.
        _restorer.EnsureBackgroundRestore(_currentUser.UserId);

        client = _sessions.Get(_currentUser.UserId.ToString());
        if (client is not null &&
            client.IsTransportConnected &&
            client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected)
        {
            return Task.FromResult<IBinollaClient?>(client);
        }

        return Task.FromResult<IBinollaClient?>(null);
    }

    private async Task<bool> IsAnalysisPausedAsync(CancellationToken ct)
    {
        if (OpenTradeGate.IsUserHeld(_currentUser.UserId))
        {
            if (OpenTradeGate.ShouldLogSkip(_currentUser.UserId))
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-STUCK1",
                    "RsiSignalAppService.IsAnalysisPausedAsync",
                    "memory_hold_skip",
                    new { userId = _currentUser.UserId.ToString("N")[..8] },
                    runId: "stuck-running");
                // #endregion
            }
            return true;
        }

        return await HasBlockingOpenTradeAsync(ct);
    }

    private async Task<bool> HasBlockingOpenTradeAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var running = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 20, status: TradeStatus.Running, ct: ct);
        var pending = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 10, status: TradeStatus.Pending, ct: ct);
        var blockers = running.Concat(pending).Where(t => OpenTradeGate.IsBlocking(t, now)).ToList();
        if (blockers.Count > 0)
        {
            var first = blockers.OrderBy(t => t.CreatedAt).First();
            if (OpenTradeGate.ShouldLogSkip(_currentUser.UserId))
            {
                // #region agent log
                ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                    "H-STUCK1",
                    "RsiSignalAppService.HasBlockingOpenTradeAsync",
                    "signal_skip_blocking_open",
                    new
                    {
                        blocking = blockers.Count,
                        running = running.Count,
                        pending = pending.Count,
                        asset = first.Asset,
                        direction = first.Direction.ToString(),
                        amount = first.Amount,
                        durationSec = first.DurationSeconds,
                        status = first.Status.ToString(),
                        ageSec = Math.Round((now - first.CreatedAt).TotalSeconds, 0),
                        tradeId = first.Id.ToString("N")[..8]
                    },
                    runId: "stuck-running");
                // #endregion
            }
        }

        return blockers.Count > 0;
    }

    /// <summary>
    /// Binolla quotes arrive after s_history/last. A short wait avoids noLiveQuote on the
    /// same tick we just subscribed.
    /// </summary>
    private static async Task<bool> WaitForFreshQuoteAsync(
        IBinollaClient client,
        string symbol,
        int maxQuoteAgeSeconds,
        CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            if (client.TryGetCachedQuote(symbol, out var quote) &&
                quote is not null &&
                (DateTimeOffset.UtcNow - quote.ReceivedAt).TotalSeconds <= maxQuoteAgeSeconds)
                return true;
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return client.TryGetCachedQuote(symbol, out var last) &&
               last is not null &&
               (DateTimeOffset.UtcNow - last.ReceivedAt).TotalSeconds <= maxQuoteAgeSeconds;
    }

    /// <summary>
    /// Overlay the latest tick as a forming candle so live RSI tracks price for display.
    /// Entry decisions use closed bars only — forming RSI never places a trade.
    /// </summary>
    private static bool ApplyLiveQuoteToCandles(
        IBinollaClient client,
        string symbol,
        List<RsiCandle> candles,
        int wirePeriod,
        DateTimeOffset now)
    {
        const int maxQuoteAgeSeconds = 15;
        if (!client.TryGetCachedQuote(symbol, out var quote) || quote is null)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE1",
                "RsiSignalAppService.ApplyLiveQuoteToCandles",
                "no_cached_quote",
                new { symbol },
                runId: "live-rsi");
            // #endregion
            return false;
        }

        var ageMs = (now - quote.ReceivedAt).TotalMilliseconds;
        if (ageMs > maxQuoteAgeSeconds * 1000)
        {
            // #region agent log
            ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
                "H-LIVE1",
                "RsiSignalAppService.ApplyLiveQuoteToCandles",
                "quote_too_stale",
                new { symbol, ageMs = Math.Round(ageMs, 0), price = quote.Price },
                runId: "live-rsi");
            // #endregion
            return false;
        }

        var quoteTs = quote.Timestamp > 0 ? quote.Timestamp : now.ToUnixTimeSeconds();
        var bucketStartUnix = MinuteBars.BucketStartUnix(quoteTs, wirePeriod);
        var bucketStart = DateTimeOffset.FromUnixTimeSeconds(bucketStartUnix);
        var price = (decimal)quote.Price;

        // Drop any bar that belongs to the current (still open) minute — replace with live quote.
        candles.RemoveAll(c =>
            c.Timestamp >= bucketStart ||
            c.EndTimestamp is null ||
            c.EndTimestamp > now);

        var lastClosedClose = candles.Count > 0 ? candles[^1].Close : (decimal?)null;
        candles.Add(new RsiCandle(
            Timestamp: bucketStart,
            Close: price,
            EndTimestamp: null));

        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug1892.Write(
            "H-LIVE1",
            "RsiSignalAppService.ApplyLiveQuoteToCandles",
            "forming_from_quote",
            new
            {
                symbol,
                price,
                lastClosedClose,
                ageMs = Math.Round(ageMs, 0),
                bucketStart = bucketStartUnix,
                closedCount = candles.Count - 1
            },
            runId: "live-rsi");
        // #endregion
        return true;
    }
}
