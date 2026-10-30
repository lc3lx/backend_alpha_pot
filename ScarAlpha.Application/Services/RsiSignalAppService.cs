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
    private readonly IAlternatingSignalService _alternatingSignalService;
    private readonly MarketAnalysisCache _analysisCache;
    private readonly IBrokerSessionManager _brokers;
    private readonly IBrokerResolver _brokerResolver;
    private readonly EmaRsiTradeTracker _emaTracker;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly IMarketingDemoService _demo;
    private readonly TradeAppService _trades;
    private readonly IBotRuntimeService _botRuntime;
    private readonly ITradeRepository _tradeRepository;
    private readonly IBinollaLinkRepository _links;
    private readonly CohortSignalCache _decisions;
    private readonly ILogger<RsiSignalAppService> _logger;

    public RsiSignalAppService(
        ICurrentUser currentUser,
        IBinollaSessionManager sessions,
        IBotAccessService botAccess,
        IRsiSignalService signalService,
        IEmaRsiSignalService emaSignalService,
        IAlternatingSignalService alternatingSignalService,
        MarketAnalysisCache analysisCache,
        IBrokerSessionManager brokers,
        IBrokerResolver brokerResolver,
        EmaRsiTradeTracker emaTracker,
        IBinollaSessionRestorer restorer,
        IMarketingDemoService demo,
        TradeAppService trades,
        IBotRuntimeService botRuntime,
        ITradeRepository tradeRepository,
        IBinollaLinkRepository links,
        CohortSignalCache decisions,
        ILogger<RsiSignalAppService> logger)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _botAccess = botAccess;
        _signalService = signalService;
        _emaSignalService = emaSignalService;
        _alternatingSignalService = alternatingSignalService;
        _analysisCache = analysisCache;
        _brokers = brokers;
        _brokerResolver = brokerResolver;
        _emaTracker = emaTracker;
        _restorer = restorer;
        _demo = demo;
        _trades = trades;
        _botRuntime = botRuntime;
        _tradeRepository = tradeRepository;
        _links = links;
        _decisions = decisions;
        _logger = logger;
    }

    public Task<StrategySignal> TryAutoExecuteAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        CancellationToken ct) =>
        ExecuteIfRequestedAsync(signal, options, autoExecute: true, ct);

    /// <summary>
    /// Live monitor for the Pairs-and-RSI door. Reads the worker's shared closed-bar
    /// cache and overlays the latest quote RSI so the board ticks every poll instead of
    /// waiting on 28 history fetches.
    /// </summary>
    public async Task<StrategyBoardResponse> GetBoardAsync(
        IReadOnlyList<string>? assets,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct).ConfigureAwait(false))
            return new StrategyBoardResponse(now, Array.Empty<StrategySignal>());

        var access = await _botAccess.CheckAsync(_currentUser.UserId, ct).ConfigureAwait(false);
        AccountAppService.EnsureConnectedForMarket(access);

        var bot = _botRuntime.Get(_currentUser.UserId);
        var strategyId = string.IsNullOrWhiteSpace(bot.StrategyId) ? "rsi" : bot.StrategyId;
        var timeframe = StrategyTimeframes.For(strategyId);
        var broker = await CurrentBrokerAsync(ct).ConfigureAwait(false);
        var analyses = _analysisCache.ListRecent(broker, timeframe, now);

        var requested = assets?
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();
        var requestedAll = requested is null
            || requested.Count == 0
            || requested.Any(a => a == "*" || a.Equals("all", StringComparison.OrdinalIgnoreCase));
        var selected = !requestedAll
            ? requested
            : bot.IsAllAssets
                ? null
                : bot.ResolvedAssets;

        var client = await EnsureLiveClientAsync(ct).ConfigureAwait(false);
        var quotes = new Dictionary<string, QuoteData>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var quote in await _brokers.RefreshMarketQuotesAsync(broker, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(quote.Pair) && quote.Price > 0)
                    quotes[quote.Pair] = quote;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RSI board quote refresh failed");
        }

        if (quotes.Count == 0 && client is not null)
        {
            try
            {
                foreach (var quote in await client.RefreshQuotesAsync(ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(quote.Pair) && quote.Price > 0)
                        quotes[quote.Pair] = quote;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RSI board fallback quote refresh failed");
            }
        }

        IReadOnlyDictionary<string, int> payouts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (client is not null && PairPayoutGate.MinPayoutPercent > 0)
        {
            try
            {
                payouts = await PairPayoutGate.BuildPayoutMapAsync(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RSI board payout snapshot failed");
            }
        }

        var rows = new List<StrategySignal>(analyses.Count);
        foreach (var analysis in analyses)
        {
            if (selected is not null && !BotAssetList.Contains(selected, analysis.Asset))
                continue;
            var quote = FindLiveQuote(quotes, client, analysis.Asset);
            rows.Add(WithEntryBlock(ToLiveBoardRow(analysis, quote, strategyId, timeframe, now), payouts));
        }

        rows.Sort((a, b) =>
        {
            var aReady = a.Signal is "Call" or "Put" && a.Backtest?.Passed == true;
            var bReady = b.Signal is "Call" or "Put" && b.Backtest?.Passed == true;
            if (aReady != bReady) return aReady ? -1 : 1;
            var aBlocked = string.IsNullOrEmpty(a.EntryBlockReason) ? 1 : 0;
            var bBlocked = string.IsNullOrEmpty(b.EntryBlockReason) ? 1 : 0;
            if (aBlocked != bBlocked) return aBlocked - bBlocked;
            var aLive = a.LiveRsi ?? a.Rsi;
            var bLive = b.LiveRsi ?? b.Rsi;
            return bLive.CompareTo(aLive);
        });

        if (quotes.Count == 0)
            _logger.LogWarning("RSI board has no live quotes (client={HasClient} rows={Rows})", client is not null, rows.Count);

        return new StrategyBoardResponse(now, rows);
    }

    /// <summary>
    /// One pair for the advisor: the board row plus the shared closed-bar analysis
    /// (regime and candles) when that bar is still on the board.
    /// </summary>
    public async Task<(StrategySignal Row, MarketAnalysis? Analysis)?> LoadAiPairAsync(
        string asset,
        CancellationToken ct)
    {
        var board = await GetBoardAsync(new[] { asset }, ct).ConfigureAwait(false);
        StrategySignal? row = null;
        foreach (var pair in board.Pairs)
        {
            if (string.Equals(pair.Asset, asset, StringComparison.OrdinalIgnoreCase)
                || BotAssetList.Contains(new[] { asset }, pair.Asset))
            {
                row = pair;
                break;
            }
        }
        if (row is null)
            return null;

        var broker = await CurrentBrokerAsync(ct).ConfigureAwait(false);
        var timeframe = StrategyTimeframes.For("rsi");
        MarketAnalysis? analysis = null;
        foreach (var item in _analysisCache.ListRecent(broker, timeframe, DateTimeOffset.UtcNow))
        {
            if (string.Equals(item.Asset, row.Asset, StringComparison.OrdinalIgnoreCase)
                || BotAssetList.Contains(new[] { row.Asset }, item.Asset))
            {
                analysis = item;
                break;
            }
        }
        return (row, analysis);
    }

    private static QuoteData? FindLiveQuote(
        IReadOnlyDictionary<string, QuoteData> quotes,
        IBrokerClient? client,
        string asset)
    {
        if (quotes.TryGetValue(asset, out var hit))
            return hit;
        if (asset.EndsWith("_otc", StringComparison.OrdinalIgnoreCase))
        {
            var weekday = asset[..^4];
            if (quotes.TryGetValue(weekday, out hit))
                return hit;
        }
        else if (quotes.TryGetValue(asset + "_otc", out hit))
        {
            return hit;
        }

        if (client is not null && client.TryGetCachedQuote(asset, out var cached) && cached is not null)
            return cached;
        return null;
    }

    /// <summary>
    /// A zone touch that stayed None tells the board why: the win-rate rule failed,
    /// the pair pays too little, or both. Mid-range rows stay blank.
    /// </summary>
    private static StrategySignal WithEntryBlock(
        StrategySignal row,
        IReadOnlyDictionary<string, int> payouts)
    {
        if (row.Signal is "Call" or "Put")
            return row;

        var closedInZone = RsiEntryLevels.IsCallRsi(row.Rsi) || RsiEntryLevels.IsPutRsi(row.Rsi);
        var live = row.LiveRsi ?? row.Rsi;
        var liveInZone = RsiEntryLevels.IsCallRsi(live) || RsiEntryLevels.IsPutRsi(live);
        if (!closedInZone && !liveInZone)
            return row;

        var zoneRsi = closedInZone ? row.Rsi : live;
        var side = RsiEntryLevels.IsCallRsi(zoneRsi) ? row.CallBacktest : row.PutBacktest;
        var parts = new List<string>(2);
        if (!RsiEntryLevels.BacktestOk(side))
            parts.Add("WIN_CONDITION");
        if (TryBoardPayout(payouts, row.Asset, out var payout) && !PairPayoutGate.IsTradable(payout))
            parts.Add($"LOW_PAYOUT:{payout}");
        if (parts.Count == 0)
            return row;
        return row with { EntryBlockReason = string.Join("|", parts) };
    }

    private static bool TryBoardPayout(IReadOnlyDictionary<string, int> payouts, string asset, out int payout)
    {
        payout = 0;
        if (payouts.Count == 0 || string.IsNullOrWhiteSpace(asset))
            return false;
        if (payouts.TryGetValue(asset, out payout))
            return true;
        var alt = asset.EndsWith("_otc", StringComparison.OrdinalIgnoreCase)
            ? asset[..^4]
            : asset + "_otc";
        return payouts.TryGetValue(alt, out payout);
    }

    private static StrategySignal ToLiveBoardRow(
        MarketAnalysis analysis,
        QuoteData? quote,
        string strategyId,
        int timeframe,
        DateTimeOffset now)
    {
        var closedRsi = analysis.Rsi;
        var livePrice = quote is not null && quote.Price > 0 ? (decimal)quote.Price : (decimal?)null;
        var overlay = LiveBoardOverlay.Rsi(analysis.Closes, livePrice);
        var liveRsi = overlay > 0m ? overlay : closedRsi;

        var secondsSinceClose = (now - analysis.ClosedBarTime).TotalSeconds;
        var withinTtl = secondsSinceClose >= 0 && secondsSinceClose <= RsiEntryLevels.SetupTtlSeconds;
        var signal = "None";
        RsiBacktestStats? entry = null;
        if (withinTtl && RsiEntryLevels.IsCallRsi(closedRsi) && RsiEntryLevels.BacktestOk(analysis.CallBacktest))
        {
            signal = "Call";
            entry = analysis.CallBacktest;
        }
        else if (withinTtl && RsiEntryLevels.IsPutRsi(closedRsi) && RsiEntryLevels.BacktestOk(analysis.PutBacktest))
        {
            signal = "Put";
            entry = analysis.PutBacktest;
        }

        var display = entry ?? RsiZoneBacktest.DisplayFor(closedRsi, analysis.CallBacktest, analysis.PutBacktest);
        return new StrategySignal(
            StrategyId: strategyId,
            Asset: analysis.Asset,
            Signal: signal,
            Rsi: closedRsi,
            CandleTime: analysis.ClosedBarTime,
            Timeframe: timeframe.ToString(),
            Backtest: display,
            LiveRsi: liveRsi,
            CallBacktest: analysis.CallBacktest,
            PutBacktest: analysis.PutBacktest);
    }

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
        var expectedPeriod = StrategyTimeframes.For(strategyId);
        if (periodSeconds != expectedPeriod)
            throw new ApiException(
                ApiErrorCodes.ValidationError,
                $"This strategy analyses the {expectedPeriod / 60}-minute timeframe.");

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

        // While a live trade is open for this user: do not auto-execute.
        if (autoExecute && !skipMarketAccess && await IsAnalysisPausedAsync(ct))
        {
            return SoftNone(asset.Trim(), periodSeconds) with
            {
                AutomationError = "OPEN_TRADE_EXISTS"
            };
        }

        var symbol = asset.Trim();
        var isAllAssets = symbol == "*" || string.Equals(symbol, "all", StringComparison.OrdinalIgnoreCase);
        if (isAllAssets)
        {
            var broker = await CurrentBrokerAsync(ct).ConfigureAwait(false);
            var cohort = SignalCohort.For(broker, strategyId, periodSeconds * options.ExpiryCandles);
            var decision = _decisions.TryGet(cohort, periodSeconds, DateTimeOffset.UtcNow);
            if (decision is { Candidates.Count: > 0 })
            {
                return decision.Candidates[0].Signal;
            }

            symbol = "EURUSD_otc";
        }

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

        if (PairPayoutGate.MinPayoutPercent > 0)
        {
            var payout = await PairPayoutGate.TryGetPayoutAsync(client, symbol, ct).ConfigureAwait(false);
            if (payout is int known && !PairPayoutGate.IsTradable(known))
            {
                return SoftNone(symbol, wirePeriod) with { AutomationError = "PAYOUT_TOO_LOW" };
            }
        }

        if (autoExecute && !skipMarketAccess && await IsAnalysisPausedAsync(ct))
        {
            return SoftNone(symbol, wirePeriod) with { AutomationError = "OPEN_TRADE_EXISTS" };
        }

        const int maxQuoteAgeSeconds = 15;
        var quoteFresh = client.TryGetCachedQuote(symbol, out var cachedQuote)
            && cachedQuote is not null
            && (DateTimeOffset.UtcNow - cachedQuote.ReceivedAt).TotalSeconds <= maxQuoteAgeSeconds;
        // GetHistory can return a dump of OLD bars. Warm whenever the series is not
        // advancing, even if a quote cache from the UI looks fresh — otherwise closing
        // the browser leaves the bot reading a frozen chart.
        if (!client.HasFreshHistory(symbol, wirePeriod) || !quoteFresh)
            client.EnsureMarketDataWarm(symbol, wirePeriod);
        MarketAnalysis? analysis = null;
        try
        {
            if (autoExecute && !skipMarketAccess && await IsAnalysisPausedAsync(ct))
            {
                return SoftNone(symbol, wirePeriod) with { AutomationError = "OPEN_TRADE_EXISTS" };
            }

            if (!quoteFresh && !skipMarketAccess)
                quoteFresh = await WaitForFreshQuoteAsync(client, symbol, maxQuoteAgeSeconds, ct);

            var now = DateTimeOffset.UtcNow;
            var period = TimeSpan.FromSeconds(wirePeriod);

            // One analysis per pair per closed bar, shared by every account: the first
            // caller fetches and computes, the rest reuse the identical closed series.
            // Two accounts on the same pair therefore always see the same signal, and
            // 100 users cost the same history traffic as one.
            var alternating = AlternatingStrategy.Is(strategyId);
            analysis = await GetSharedAnalysisAsync(
                client, symbol, wirePeriod, options, now, ct,
                minCandles: alternating ? AlternatingOptions.Default.PatternLength + 1 : null);
            if (analysis is null)
                return SoftNone(symbol, wirePeriod) with { AutomationError = "INSUFFICIENT_HISTORY" };

            var candles = analysis.ClosedCandles.ToList();
            if (candles.Count == 0)
                return SoftNone(symbol, wirePeriod, 0m, analysis);

            // Pine's checkpoint + win/loss counters, advanced on the closed series.
            _emaTracker.Resolve(_currentUser.UserId, symbol, analysis.ClosedCandles, wirePeriod);

            if (autoExecute && !skipMarketAccess && await IsAnalysisPausedAsync(ct))
            {
                return SoftNone(symbol, wirePeriod, analysis.Rsi, analysis) with { AutomationError = "OPEN_TRADE_EXISTS" };
            }

            _ = ApplyLiveQuoteToCandles(client, symbol, candles, wirePeriod, now);

            var lastClosed = candles.LastOrDefault(c => c.Timestamp + period <= now);
            if (lastClosed is { } closedBar &&
                (now - (closedBar.Timestamp + period)).TotalSeconds > wirePeriod * 3)
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
                var staleRsi = analysis.Rsi > 0m ? analysis.Rsi : (analysis.Closes.Count >= options.Period + 1
                    ? Math.Round(Indicators.Rsi(analysis.Closes, options.Period)!.Value, 2, MidpointRounding.AwayFromZero)
                    : 0m);
                return SoftNone(symbol, wirePeriod, staleRsi, analysis);
            }

            // EMA runs purely on its own settings — no coupling to the regime switch.
            var emaOptions = EmaRsiOptions.FromBotDurationSeconds(options.ExpiryCandles * 60)
                with { UseTrendFilter = EmaTrendFilterEnabled };

            if (alternating)
            {
                var altSignal = await _alternatingSignalService.GetSignalAsync(
                    userId: _currentUser.UserId,
                    asset: symbol,
                    candles: candles,
                    options: AlternatingOptions.Default,
                    now: now,
                    ct: ct);

                altSignal = altSignal with
                {
                    Regime = analysis.Regime?.Regime ?? MarketRegime.Unclear,
                    RegimeReason = analysis.Regime?.Reason,
                    RegimeApplied = analysis.Regime is not null,
                    RelativeVolume = analysis.Regime?.RelativeVolume,
                    VolumeOk = analysis.Regime?.VolumeOk ?? true,
                };

                return await ExecuteIfRequestedAsync(altSignal, options, autoExecute, ct, analysis.Regime);
            }

            // iPhone Journey reads only the closed series — never the live-quote overlay —
            // because the rule was measured on closed bars and enters at their close.
            if (IPhoneJourneyStrategy.Is(strategyId))
            {
                var journeySignal = IPhoneJourneyStrategy.BuildSignal(
                    symbol,
                    analysis.Closes,
                    analysis.ClosedCandles[^1].Timestamp,
                    wirePeriod,
                    now);
                return await ExecuteIfRequestedAsync(journeySignal, options, autoExecute, ct, analysis.Regime);
            }

            // Smart mode hands engine choice to the regime; an explicit choice is kept.
            var engineId = RegimeRouting.IsSmart(strategyId)
                ? RegimeRouting.EngineFor(analysis.Regime?.Regime ?? MarketRegime.Unclear)
                : strategyId;
            if (engineId is null)
            {
                return SoftNone(symbol, wirePeriod, analysis.Rsi, analysis) with
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
                    ct: ct,
                    cachedCallBacktest: analysis.CallBacktest,
                    cachedPutBacktest: analysis.PutBacktest);

            signal = signal with
            {
                Regime = analysis.Regime?.Regime ?? MarketRegime.Unclear,
                RegimeReason = analysis.Regime?.Reason,
                RegimeApplied = analysis.Regime is not null,
                RelativeVolume = analysis.Regime?.RelativeVolume,
                VolumeOk = analysis.Regime?.VolumeOk ?? true,
            };

            if (!IsEma(engineId) && !alternating)
                signal = AttachReadyBacktests(signal, analysis);

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
            return SoftNone(symbol, wirePeriod, analysis?.Rsi ?? 0m, analysis);
        }
        catch (OperationCanceledException)
        {
            return SoftNone(symbol, wirePeriod, analysis?.Rsi ?? 0m, analysis);
        }
        catch (BinollaConnectionException)
        {
            return SoftNone(symbol, wirePeriod, analysis?.Rsi ?? 0m, analysis);
        }
        catch (ApiException ex) when (ex.Code == ApiErrorCodes.ValidationError)
        {
            // Insufficient candles — soft None, not 400/500 spam.
            return SoftNone(symbol, wirePeriod, analysis?.Rsi ?? 0m, analysis);
        }
        catch (ApiException) { throw; }
        catch (Exception)
        {
            return SoftNone(symbol, wirePeriod, analysis?.Rsi ?? 0m, analysis);
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
        IBrokerClient client,
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
            var trend = await _analysisCache.GetOrAddAsync(client.Broker, symbol, tf, now, async _ =>
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
        IBrokerClient client,
        string symbol,
        int wirePeriod,
        RsiStrategyOptions options,
        DateTimeOffset now,
        CancellationToken ct,
        int? minCandles = null) =>
        _analysisCache.GetOrAddAsync(client.Broker, symbol, wirePeriod, now, async _ =>
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
            // The alternating rule reads candle bodies only, so it must not be blocked by
            // an RSI warmup it never uses. Indicators below are then computed
            // opportunistically and left absent when there is not enough history.
            var indicatorWarmup = Math.Max(
                IndicatorWarmup.ForRsi(options.Period),
                options.Period + options.ExpiryCandles + 2);
            var warmup = minCandles ?? indicatorWarmup;
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
                if (prepared.Count >= options.Period + 1)
                {
                    var partialCloses = prepared.Closes;
                    var partialRsi = Math.Round(Indicators.Rsi(partialCloses, options.Period)!.Value, 2, MidpointRounding.AwayFromZero);
                    // The board and the entry gate both need a number. A short tail still
                    // has a backtest over the candles that exist; only the rate is thinner.
                    RsiBacktestStats? partialCall = null;
                    RsiBacktestStats? partialPut = null;
                    if (prepared.Count >= options.Period + options.ExpiryCandles + 2)
                        (partialCall, partialPut) = RsiZoneBacktest.Evaluate(partialCloses, options);
                    return new MarketAnalysis(
                        Asset: symbol,
                        TimeframeSeconds: wirePeriod,
                        ClosedBarTime: prepared.Last.Timestamp + period,
                        ClosedCandles: prepared.Closed,
                        Closes: partialCloses,
                        Rsi: partialRsi,
                        CallBacktest: partialCall,
                        PutBacktest: partialPut,
                        Regime: null);
                }
                return null;
            }

            var closes = prepared.Closes;
            var indicatorsReady = prepared.Count >= indicatorWarmup;

            var rsi = indicatorsReady
                ? Math.Round(Indicators.Rsi(closes, options.Period)!.Value, 2, MidpointRounding.AwayFromZero)
                : 0m;
            RsiBacktestStats? call = null;
            RsiBacktestStats? put = null;
            RegimeSnapshot? regime = null;
            if (indicatorsReady)
            {
                (call, put) = RsiZoneBacktest.Evaluate(closes, options);
                // Same closed series, so every account sees the same regime for this bar.
                regime = MarketRegimeClassifier.Analyze(prepared.Closed, MarketRegimeOptions.Default);
            }

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

    /// <summary>
    /// Pins both backtests onto the signal so the UI/worker already have them while
    /// closed RSI is still mid-range. Entry still waits for 25/75; the numbers do not.
    /// </summary>
    private static StrategySignal AttachReadyBacktests(StrategySignal signal, MarketAnalysis analysis)
    {
        var call = signal.CallBacktest ?? analysis.CallBacktest;
        var put = signal.PutBacktest ?? analysis.PutBacktest;
        var display = signal.Backtest ?? RsiZoneBacktest.DisplayFor(signal.Rsi, call, put);
        return signal with
        {
            Backtest = display,
            CallBacktest = call,
            PutBacktest = put
        };
    }

    private static StrategySignal SoftNone(
        string symbol,
        int periodSeconds,
        decimal rsi = 0m,
        MarketAnalysis? analysis = null)
    {
        var resolvedRsi = rsi > 0m ? rsi : analysis?.Rsi ?? 0m;
        var call = analysis?.CallBacktest;
        var put = analysis?.PutBacktest;
        return new(
            StrategyId: "rsi",
            Asset: symbol,
            Signal: "None",
            Rsi: resolvedRsi,
            CandleTime: analysis?.ClosedBarTime ?? DateTimeOffset.UtcNow,
            Timeframe: periodSeconds.ToString(),
            Backtest: RsiZoneBacktest.DisplayFor(resolvedRsi, call, put),
            CallBacktest: call,
            PutBacktest: put);
    }

    private async Task<StrategySignal> ExecuteIfRequestedAsync(
        StrategySignal signal,
        RsiStrategyOptions options,
        bool autoExecute,
        CancellationToken ct,
        RegimeSnapshot? regime = null)
    {
        var userId = _currentUser.UserId;

        // ONLY the server-side worker places bot trades.
        //
        // This used to ignore `autoExecute` and place from any caller whose bot was
        // Running, which made the read-only signal endpoint a third trade source next to
        // the worker. Every UI polls that endpoint for the pair it happens to be showing,
        // so a user with the web app and the Mini App open at once had three independent
        // placers running on three different pairs. The idempotency key carries the asset
        // (bot:{strategy}:{asset}:{bar}:{direction}), so different pairs produce different
        // keys and nothing deduplicated them — the user simply got two trades.
        //
        // The worker is the single writer; polling is display only.
        if (!autoExecute)
            return signal;

        var bot = _botRuntime.Get(userId);
        if (bot.State != BotRunState.Running)
            return signal;

        // Only the strategy the user selected may trade. The Mini App polls the RSI
        // endpoint directly, so without this a bot configured for EMA could still have
        // an RSI setup placed underneath it.
        // A smart bot legitimately runs either engine, so the mismatch check only
        // applies when the user pinned one.
        if (AlternatingStrategy.Is(bot.StrategyId) != AlternatingStrategy.Is(signal.StrategyId))
            return signal with { AutomationError = "STRATEGY_MISMATCH", Signal = "None" };

        // Without this an RSI setup polled by the Mini App would pass the EMA check below
        // (neither side is EMA) and trade underneath an iPhone Journey bot.
        if (IPhoneJourneyStrategy.Is(bot.StrategyId) != IPhoneJourneyStrategy.Is(signal.StrategyId))
            return signal with { AutomationError = "STRATEGY_MISMATCH", Signal = "None" };

        if (IPhoneJourneyStrategy.Is(signal.StrategyId))
        {
            if (IPhoneJourneyStrategy.IsPaused(userId, DateTimeOffset.UtcNow, out _))
                return signal with { AutomationError = "LOSS_STREAK_PAUSE", Signal = "None" };
            // The shared gate allows 30s; the journey was measured entering within 25s of
            // the close, and a setup scanned at 20s can reach execution after that.
            if ((DateTimeOffset.UtcNow - signal.CandleTime).TotalSeconds > IPhoneJourneyStrategy.EntryWindowSeconds)
                return signal with { AutomationError = "SETUP_EXPIRED", Signal = "None" };
        }

        if (!RegimeRouting.IsSmart(bot.StrategyId) &&
            !AlternatingStrategy.Is(bot.StrategyId) &&
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

        var journey = IPhoneJourneyStrategy.Is(signal.StrategyId);
        if (PairPayoutGate.MinPayoutPercent > 0 || journey)
        {
            var client = await EnsureLiveClientAsync(ct);
            if (client is not null)
            {
                var payout = await PairPayoutGate.TryGetPayoutAsync(client, signal.Asset, ct).ConfigureAwait(false);
                if (payout is int known && !PairPayoutGate.IsTradable(known))
                    return signal with { AutomationError = "PAYOUT_TOO_LOW", Signal = "None" };
                // The journey's own floor, stricter than the global one: breakeven is
                // 1/(1+payout), so every point of payout below 90 raises the bar the rule
                // already fails to clear. An unknown payout is refused, not assumed.
                if (journey && (payout is not int jp || jp < IPhoneJourneyStrategy.MinPayoutPercent))
                    return signal with { AutomationError = "PAYOUT_TOO_LOW", Signal = "None" };
            }
        }

        var dailyLimit = await GetReachedDailyLimitAsync(bot, ct);
        if (dailyLimit is not null)
        {
            _botRuntime.Stop(userId, dailyLimit);
            return signal with { AutomationError = dailyLimit };
        }

        // One live trade at a time — expired/stuck Running rows must not freeze the bot.
        var now = DateTimeOffset.UtcNow;
        var accountType = await CurrentAccountTypeAsync(ct);
        var runningRows = await _tradeRepository.ListByUserAsync(
            userId, take: 20, status: TradeStatus.Running, accountType: accountType, ct: ct);
        var pendingRows = await _tradeRepository.ListByUserAsync(
            userId, take: 10, status: TradeStatus.Pending, accountType: accountType, ct: ct);
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
                userId, take: 20, status: TradeStatus.Running, accountType: accountType, ct: ct);
            pendingRows = await _tradeRepository.ListByUserAsync(
                userId, take: 10, status: TradeStatus.Pending, accountType: accountType, ct: ct);
            if (runningRows.Concat(pendingRows).Any(t => OpenTradeGate.IsBlocking(t, DateTimeOffset.UtcNow)))
                return signal with { AutomationError = "OPEN_TRADE_EXISTS" };

            // Analyse 1-minute bars; buy the 3–5 minute option the backtest used.
            // A stored 60s value is the chart timeframe, not a turbo deal.
            var expiryCandles = signal.Backtest.ExpiryCandles;
            if (!AlternatingStrategy.Is(signal.StrategyId) && expiryCandles is < 3 or > 5)
                expiryCandles = options.ExpiryCandles is >= 3 and <= 5 ? options.ExpiryCandles : 5;
            var durationSeconds = AlternatingStrategy.Is(signal.StrategyId)
                ? AlternatingStrategy.TimeframeSeconds
                : RsiStrategyOptions.TradeDurationSeconds(bot.DurationSeconds, expiryCandles);
            OpenTradeGate.MarkUserHeld(userId, durationSeconds);

            var strategyKey = AlternatingStrategy.Is(signal.StrategyId)
                ? AlternatingStrategy.Id
                : IPhoneJourneyStrategy.Is(signal.StrategyId) ? IPhoneJourneyStrategy.Id
                : IsEma(signal.StrategyId) ? "ema" : "rsi";
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
                    // The journey's stake is flat and bounded server-side, whatever the
                    // runtime holds: no client can turn it into a progression.
                    Amount: journey ? IPhoneJourneyStrategy.ClampStake(bot.Amount) : bot.Amount,
                    DurationSeconds: durationSeconds,
                    StrategyId: strategyKey), key, ct);

                if (AlternatingStrategy.Is(signal.StrategyId))
                {
                    _alternatingSignalService.MarkSignalEmitted(
                        userId, signal.Asset, AlternatingStrategy.TimeframeSeconds, signal.CandleTime);
                }
                else if (IsEma(signal.StrategyId))
                {
                    _emaSignalService.MarkSignalEmitted(
                        userId, signal.Asset, options.TimeframeSeconds, signal.CandleTime);

                    // Pine scores from the close of the signal bar, which is where we entered.
                    var entryClose = _analysisCache
                        .TryGet(
                            await CurrentBrokerAsync(ct).ConfigureAwait(false),
                            signal.Asset,
                            options.TimeframeSeconds,
                            DateTimeOffset.UtcNow)?
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
                else if (journey)
                {
                    // Nothing to mark: the idempotency key carries the bar and direction,
                    // so the same setup cannot place twice.
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
                // A timeout often means the order landed and the ack was missed.
                // Releasing here is what let the next pair open on top of it.
                if (ex.Code is ApiErrorCodes.BinollaNotConnected
                    or ApiErrorCodes.BinollaSessionExpired
                    or ApiErrorCodes.MarketUnavailable
                    or ApiErrorCodes.BinollaMarketUnavailable
                    or ApiErrorCodes.ValidationError
                    or ApiErrorCodes.InvalidTrade
                    or ApiErrorCodes.RealTradingDisabled
                    or ApiErrorCodes.StrategyDisabled)
                {
                    OpenTradeGate.ReleaseUser(userId);
                }
                if (ex.Code == ApiErrorCodes.InsufficientBalance)
                {
                    // Stake bigger than the book: do not retry this user every second
                    // for the rest of the 30s TTL — that is what filled the logs while
                    // funded bots sat behind the old 90s hold.
                    OpenTradeGate.MarkUserHeld(userId, RsiEntryLevels.SetupTtlSeconds);
                }
                if (OpenTradeGate.ShouldLogSkip(userId))
                {
                    _logger.LogWarning(
                        "RSI place skipped {Asset} {Direction} code={Code} detail={Detail}",
                        signal.Asset,
                        signal.Signal,
                        ex.Code,
                        ex.Message);
                }
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

    public async Task<string?> GetReachedDailyLimitAsync(BotRuntimeConfig bot, CancellationToken ct)
    {
        if ((!bot.AutoStopAtProfit || bot.DailyProfitTarget <= 0m) &&
            (!bot.AutoStopAtLoss || bot.DailyLossLimit <= 0m))
            return null;

        // Session baseline: after user presses Start again, prior PnL does not count.
        var startOfUtcDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var from = bot.PnlSessionStartedAt ?? startOfUtcDay;

        var trades = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 1000, accountType: await CurrentAccountTypeAsync(ct), ct: ct);
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

    /// <summary>
    /// The caller's live client on whichever venue they are linked to.
    ///
    /// <para>Resolving the broker here — rather than assuming Binolla — is what lets one
    /// strategy implementation serve both venues.</para>
    /// </summary>
    private async Task<IBrokerClient?> EnsureLiveClientAsync(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var broker = await _brokerResolver.GetAsync(userId, ct).ConfigureAwait(false);

        if (AsLiveClient(_brokers.Get(userId, broker)) is { } live)
            return live;

        // Market data (quotes/candles) may ride a live sibling session. Balance and
        // orders never come through here — PlaceTrade / GetBalance use Get(userId).
        if (!string.Equals(broker, Brokers.Binolla, StringComparison.OrdinalIgnoreCase))
        {
            if (AsLiveClient(_brokers.GetAny(broker)) is { } shared)
                return shared;
        }

        return null;
    }

    private static IBrokerClient? AsLiveClient(IBrokerClient? client) =>
        client is not null
        && client.IsTransportConnected
        && client.Lifecycle is SessionLifecycleState.Connected or SessionLifecycleState.Reconnected
            ? client
            : null;

    /// <summary>The venue the current caller trades on. Cached; see IBrokerResolver.</summary>
    private Task<string> CurrentBrokerAsync(CancellationToken ct) =>
        _brokerResolver.GetAsync(_currentUser.UserId, ct);

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

    private async Task<BinollaAccountType> CurrentAccountTypeAsync(CancellationToken ct)
    {
        var link = await _links.GetByUserIdAsync(_currentUser.UserId, ct);
        return link?.AccountType ?? BinollaAccountType.Demo;
    }

    private async Task<bool> HasBlockingOpenTradeAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var accountType = await CurrentAccountTypeAsync(ct);
        var running = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 20, status: TradeStatus.Running, accountType: accountType, ct: ct);
        var pending = await _tradeRepository.ListByUserAsync(
            _currentUser.UserId, take: 10, status: TradeStatus.Pending, accountType: accountType, ct: ct);
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
        IBrokerClient client,
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
        IBrokerClient client,
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
        if (now.ToUnixTimeSeconds() - quoteTs > wirePeriod * 2)
            quoteTs = now.ToUnixTimeSeconds();
        var bucketStartUnix = MinuteBars.BucketStartUnix(quoteTs, wirePeriod);
        var bucketStart = DateTimeOffset.FromUnixTimeSeconds(bucketStartUnix);
        var price = (decimal)quote.Price;

        // Drop any bar that belongs to the current (still open) minute — replace with live quote.
        candles.RemoveAll(c =>
            c.Timestamp >= bucketStart ||
            c.EndTimestamp is null ||
            c.EndTimestamp > now);

        // Vendor history can freeze while ticks still arrive. A live quote means the
        // previous minute has closed — fill the gap so SoftNone does not block every
        // entry for a quarter of an hour.
        if (candles.Count > 0)
        {
            var cursor = candles[^1].Timestamp.ToUnixTimeSeconds() + wirePeriod;
            var filled = 0;
            while (cursor < bucketStartUnix && filled < 180)
            {
                var lastGap = cursor + wirePeriod >= bucketStartUnix;
                candles.Add(new RsiCandle(
                    Timestamp: DateTimeOffset.FromUnixTimeSeconds(cursor),
                    Close: lastGap ? price : candles[^1].Close,
                    EndTimestamp: DateTimeOffset.FromUnixTimeSeconds(cursor + wirePeriod)));
                cursor += wirePeriod;
                filled++;
            }
        }

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
