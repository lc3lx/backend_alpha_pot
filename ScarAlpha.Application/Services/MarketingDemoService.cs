using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Synthetic live-looking payloads for marketing demo accounts. Never talks to Binolla.
/// Numbers are driven by admin-configured <see cref="MarketingDemoConfigDto"/> on the user.
/// </summary>
public interface IMarketingDemoService
{
    Task<bool> IsMarketingDemoAsync(Guid userId, CancellationToken ct = default);
    void WarmConfig(User user);
    MarketingDemoConfigDto GetConfig(Guid userId);
    BinollaBalanceDto BuildBalance(Guid userId);
    BinollaStatusDto BuildStatus(Guid userId);
    AccountStatusResponse BuildAccountStatus();
    AccountSubscriptionResponse BuildSubscription(Guid userId, DateTimeOffset? createdAt);
    ActivationHistoryResponse BuildActivationHistory(Guid userId);
    TradeListResponse BuildTrades(Guid userId, int page, int pageSize, string? status, string? asset);
    TradeDto? FindTrade(Guid userId, Guid tradeId);
    TradeDto PlaceSimulatedTrade(Guid userId, PlaceTradeRequest request, string idempotencyKey);
    NotificationListResponse BuildNotifications(Guid userId);
    NotificationDto? FindNotification(Guid userId, Guid id);
    NotificationDto MarkNotificationRead(Guid userId, Guid id);
    NotificationListResponse MarkAllNotificationsRead(Guid userId);
    MarketAssetsResponse BuildAssets();
    MarketPriceResponse BuildPrice(string asset);
    MarketCandlesResponse BuildCandles(string asset, int periodSeconds);
    StrategySignal BuildRsiSignal(string asset, int periodSeconds);
}

public sealed class MarketingDemoService : IMarketingDemoService
{
    private static readonly string[] Assets =
    [
        "EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDUSD_otc", "EURGBP_otc", "USDCAD_otc"
    ];

    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, TradeDto>> PlacedByUser = new();
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, bool>> ReadNotifications = new();

    private readonly IUserRepository _users;
    private readonly Dictionary<Guid, MarketingDemoConfigDto> _configCache = new();

    public MarketingDemoService(IUserRepository users) => _users = users;

    public async Task<bool> IsMarketingDemoAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user?.IsMarketingDemo != true)
            return false;

        WarmConfig(user);
        return true;
    }

    public void WarmConfig(User user)
    {
        if (!user.IsMarketingDemo)
            return;
        _configCache[user.Id] = MarketingDemoConfigStore.FromUser(user);
    }

    public MarketingDemoConfigDto GetConfig(Guid userId) =>
        _configCache.TryGetValue(userId, out var c) ? c : MarketingDemoConfigStore.Default;

    public BinollaBalanceDto BuildBalance(Guid userId)
    {
        var balance = LiveBalance(userId);
        return new BinollaBalanceDto(
            Connected: true,
            AccountType: "Demo",
            DemoBalance: balance,
            RealBalance: 0m,
            CurrentBalance: balance);
    }

    public BinollaStatusDto BuildStatus(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        return new BinollaStatusDto(
            Connected: true,
            AccountType: "Demo",
            Status: nameof(BinollaLinkStatus.Connected),
            LastConnectedAt: now.AddMinutes(-2),
            Balance: LiveBalance(userId),
            Lifecycle: "Connected",
            WebSocketConnected: true);
    }

    public AccountStatusResponse BuildAccountStatus() =>
        new(
            BinollaConnected: true,
            AccountType: "Demo",
            AdminApproved: true,
            ApprovalStatus: nameof(AdminApprovalStatus.Approved),
            BotAccess: "Allowed");

    public AccountSubscriptionResponse BuildSubscription(Guid userId, DateTimeOffset? createdAt)
    {
        var started = createdAt ?? DateTimeOffset.UtcNow.AddDays(-14);
        var plan = GetConfig(userId).PlanName ?? "Pro (marketing demo)";
        return new AccountSubscriptionResponse(
            PlanName: plan,
            Status: "active",
            StatusLabel: "Allowed",
            ApprovalStatus: nameof(AdminApprovalStatus.Approved),
            StartedAt: started,
            ApprovedAt: started.AddHours(1),
            KeyUsedLabel: "Marketing demo access");
    }

    public ActivationHistoryResponse BuildActivationHistory(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivationHistoryResponse(
        [
            new ActivationHistoryItemDto(
                Id: DemoGuid(userId, "activation", 1).ToString(),
                KeyLabel: "Marketing demo enabled",
                Status: "active",
                StatusLabel: "Approved:True",
                PreviousState: "Pending:False",
                NewState: "Approved:True",
                CreatedAt: now.AddDays(-14))
        ]);
    }

    public TradeListResponse BuildTrades(Guid userId, int page, int pageSize, string? status, string? asset)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var all = AllTrades(userId);
        IEnumerable<TradeDto> filtered = all;
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TradeStatus>(status, ignoreCase: true, out var parsed))
        {
            filtered = filtered.Where(t =>
                string.Equals(t.Status, parsed.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(asset))
        {
            var a = asset.Trim();
            filtered = filtered.Where(t => t.Asset.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        var slice = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new TradeListResponse(slice, list.Count, page, pageSize);
    }

    public TradeDto? FindTrade(Guid userId, Guid tradeId) =>
        AllTrades(userId).FirstOrDefault(t => Guid.TryParse(t.Id, out var id) && id == tradeId);

    public TradeDto PlaceSimulatedTrade(Guid userId, PlaceTradeRequest request, string idempotencyKey)
    {
        var bag = PlacedByUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, TradeDto>(StringComparer.Ordinal));
        if (bag.TryGetValue(idempotencyKey, out var existing))
            return existing;

        var cfg = GetConfig(userId);
        var now = DateTimeOffset.UtcNow;
        var direction = (request.Direction ?? "CALL").Trim().ToUpperInvariant();
        if (direction is not ("CALL" or "PUT" or "UP" or "DOWN"))
            throw new ApiException(ApiErrorCodes.InvalidTrade, "direction must be CALL or PUT.");
        direction = direction is "UP" or "CALL" ? "CALL" : "PUT";

        var winProb = (double)(cfg.WinRatePercent / 100m);
        var win = StableBool(userId, now.ToUnixTimeSeconds(), winProb);
        var amount = request.Amount;
        var pnl = win ? Math.Round(amount * 0.87m, 2) : -amount;
        var endsAt = now.AddSeconds(Math.Clamp(request.DurationSeconds, 5, 3600));
        var stillRunning = endsAt > now;

        var trade = new TradeDto(
            Id: Guid.NewGuid().ToString(),
            BinollaOrderId: $"mkt-demo-{Guid.NewGuid():N}"[..24],
            Asset: string.IsNullOrWhiteSpace(request.Asset) ? Assets[0] : request.Asset.Trim(),
            Direction: direction,
            Amount: amount,
            DurationSeconds: request.DurationSeconds,
            Status: stillRunning ? nameof(TradeStatus.Running) : (win ? nameof(TradeStatus.Profit) : nameof(TradeStatus.Loss)),
            Pnl: stillRunning ? null : pnl,
            ErrorCode: null,
            CreatedAt: now,
            UpdatedAt: now);

        bag[idempotencyKey] = trade;
        return trade;
    }

    public NotificationListResponse BuildNotifications(Guid userId)
    {
        var items = BuildNotificationItems(userId);
        var unread = items.Count(n => !n.Read);
        return new NotificationListResponse(items, unread);
    }

    public NotificationDto? FindNotification(Guid userId, Guid id) =>
        BuildNotificationItems(userId).FirstOrDefault(n => Guid.TryParse(n.Id, out var nid) && nid == id);

    public NotificationDto MarkNotificationRead(Guid userId, Guid id)
    {
        var item = FindNotification(userId, id)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Notification not found.", 404);
        ReadNotifications.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, bool>())[id] = true;
        return item with { Read = true };
    }

    public NotificationListResponse MarkAllNotificationsRead(Guid userId)
    {
        var bag = ReadNotifications.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, bool>());
        foreach (var n in BuildNotificationItems(userId))
        {
            if (Guid.TryParse(n.Id, out var id))
                bag[id] = true;
        }

        return BuildNotifications(userId);
    }

    public MarketAssetsResponse BuildAssets()
    {
        var assets = Assets.Select((symbol, i) => new MarketAssetDto(
            Symbol: symbol,
            Name: symbol.Replace("_otc", "", StringComparison.OrdinalIgnoreCase),
            Available: true,
            Payout: 80 + (i % 8))).ToList();
        return new MarketAssetsResponse(assets);
    }

    public MarketPriceResponse BuildPrice(string asset)
    {
        var symbol = string.IsNullOrWhiteSpace(asset) ? Assets[0] : asset.Trim();
        var now = DateTimeOffset.UtcNow;
        var basePrice = BasePrice(symbol);
        var tick = (decimal)Math.Sin(now.ToUnixTimeSeconds() / 7.0 + Hash(symbol) % 10) * 0.00035m;
        return new MarketPriceResponse(symbol, Math.Round(basePrice + tick, 5), now);
    }

    public MarketCandlesResponse BuildCandles(string asset, int periodSeconds)
    {
        var symbol = string.IsNullOrWhiteSpace(asset) ? Assets[0] : asset.Trim();
        periodSeconds = Math.Clamp(periodSeconds, 1, 14400);
        var now = DateTimeOffset.UtcNow;
        var aligned = new DateTimeOffset(
            now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        aligned = aligned.AddSeconds(-(aligned.ToUnixTimeSeconds() % periodSeconds));

        var candles = new List<MarketCandleDto>(60);
        var price = BasePrice(symbol);
        var seed = Hash(symbol) + Hash(aligned.ToString("O"));
        for (var i = 59; i >= 0; i--)
        {
            var t = aligned.AddSeconds(-i * periodSeconds);
            var wave = (decimal)Math.Sin((seed + i) * 0.37) * 0.0008m;
            var open = Math.Round(price + wave, 5);
            var close = Math.Round(open + (decimal)Math.Cos((seed + i) * 0.51) * 0.0004m, 5);
            var high = Math.Max(open, close) + 0.00015m;
            var low = Math.Min(open, close) - 0.00015m;
            candles.Add(new MarketCandleDto(t, open, high, low, close));
            price = close;
        }

        return new MarketCandlesResponse(symbol, periodSeconds, candles);
    }

    public StrategySignal BuildRsiSignal(string asset, int periodSeconds)
    {
        var symbol = string.IsNullOrWhiteSpace(asset) ? Assets[0] : asset.Trim();
        periodSeconds = Math.Clamp(periodSeconds, 1, 14400);
        var now = DateTimeOffset.UtcNow;
        var bucket = now.ToUnixTimeSeconds() / Math.Max(15, periodSeconds / 4);
        var rsi = 28m + StableInt(Hash(symbol) ^ (int)bucket, 45);
        var signal = rsi <= 32 ? "CALL" : rsi >= 68 ? "PUT" : "NONE";
        return new StrategySignal(
            StrategyId: "rsi",
            Asset: symbol,
            Signal: signal,
            Rsi: Math.Round(rsi, 2),
            CandleTime: now.AddSeconds(-(now.ToUnixTimeSeconds() % periodSeconds)),
            Timeframe: $"{periodSeconds}s");
    }

    private List<TradeDto> AllTrades(Guid userId)
    {
        var synthetic = BuildHistoryFromConfig(userId);
        if (PlacedByUser.TryGetValue(userId, out var bag) && !bag.IsEmpty)
        {
            var live = bag.Values
                .Select(t => RefreshPlacedTrade(userId, t))
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
            return live.Concat(synthetic).OrderByDescending(t => t.CreatedAt).ToList();
        }

        return synthetic;
    }

    private TradeDto RefreshPlacedTrade(Guid userId, TradeDto trade)
    {
        if (!string.Equals(trade.Status, nameof(TradeStatus.Running), StringComparison.OrdinalIgnoreCase))
            return trade;

        var endsAt = trade.CreatedAt.AddSeconds(trade.DurationSeconds);
        if (endsAt > DateTimeOffset.UtcNow)
            return trade;

        var winProb = (double)(GetConfig(userId).WinRatePercent / 100m);
        var win = StableBool(Guid.Parse(trade.Id), trade.CreatedAt.ToUnixTimeSeconds(), winProb);
        var pnl = win ? Math.Round(trade.Amount * 0.87m, 2) : -trade.Amount;
        return trade with
        {
            Status = win ? nameof(TradeStatus.Profit) : nameof(TradeStatus.Loss),
            Pnl = pnl,
            UpdatedAt = endsAt
        };
    }

    private List<TradeDto> BuildHistoryFromConfig(Guid userId)
    {
        var cfg = GetConfig(userId);
        var now = DateTimeOffset.UtcNow;
        var list = new List<TradeDto>();

        if (cfg.SampleTrades is { Count: > 0 })
        {
            for (var i = 0; i < cfg.SampleTrades.Count; i++)
            {
                var seed = cfg.SampleTrades[i];
                var created = now.AddMinutes(-seed.MinutesAgo);
                var stillRunning = string.Equals(seed.Status, nameof(TradeStatus.Running), StringComparison.OrdinalIgnoreCase)
                                   && created.AddSeconds(seed.DurationSeconds) > now;
                list.Add(new TradeDto(
                    Id: DemoGuid(userId, "seed", i + 1).ToString(),
                    BinollaOrderId: $"mkt-demo-s-{i + 1}",
                    Asset: seed.Asset,
                    Direction: seed.Direction,
                    Amount: seed.Amount,
                    DurationSeconds: seed.DurationSeconds,
                    Status: stillRunning ? nameof(TradeStatus.Running) : seed.Status,
                    Pnl: stillRunning ? null : seed.Pnl,
                    ErrorCode: null,
                    CreatedAt: created,
                    UpdatedAt: stillRunning ? created : created.AddSeconds(seed.DurationSeconds)));
            }

            return list.OrderByDescending(t => t.CreatedAt).ToList();
        }

        if (cfg.IncludeRunningTrade)
        {
            var runBucket = now.ToUnixTimeSeconds() / 90;
            var runAsset = Assets[StableInt(Hash(userId) ^ (int)runBucket, Assets.Length)];
            var runId = DemoGuid(userId, "running", (int)runBucket);
            var runCreated = now.AddSeconds(-(now.ToUnixTimeSeconds() % 90));
            var runAmount = Math.Max(cfg.DefaultTradeAmount, 10m);
            list.Add(new TradeDto(
                Id: runId.ToString(),
                BinollaOrderId: $"mkt-demo-run-{runBucket}",
                Asset: runAsset,
                Direction: StableBool(userId, runBucket, 0.55) ? "CALL" : "PUT",
                Amount: runAmount,
                DurationSeconds: 60,
                Status: nameof(TradeStatus.Running),
                Pnl: null,
                ErrorCode: null,
                CreatedAt: runCreated,
                UpdatedAt: runCreated));
        }

        var count = cfg.HistoryTradeCount;
        var winCount = Math.Clamp((int)Math.Round(count * (double)(cfg.WinRatePercent / 100m)), 0, count);
        var lossCount = count - winCount;
        if (lossCount == 0 && cfg.TotalLoss > 0 && count > 1)
        {
            lossCount = 1;
            winCount = count - 1;
        }

        var outcomes = new bool[count];
        for (var i = 0; i < winCount; i++)
            outcomes[i] = true;
        // Stable Fisher–Yates so wins/losses interleave without changing totals.
        for (var i = count - 1; i > 0; i--)
        {
            var j = StableInt(Hash(userId) ^ (i * 7919), i + 1);
            (outcomes[i], outcomes[j]) = (outcomes[j], outcomes[i]);
        }

        var winAmounts = SplitTotal(cfg.TotalProfit, winCount, userId, 11);
        var lossAmounts = SplitTotal(cfg.TotalLoss, lossCount, userId, 29);
        var winIdx = 0;
        var lossIdx = 0;

        for (var i = 1; i <= count; i++)
        {
            var isWin = outcomes[i - 1];
            decimal amount;
            decimal pnl;
            string status;
            if (isWin)
            {
                pnl = winAmounts[Math.Min(winIdx, Math.Max(0, winAmounts.Length - 1))];
                amount = pnl > 0 ? Math.Round(pnl / 0.87m, 2) : cfg.DefaultTradeAmount;
                winIdx++;
                status = nameof(TradeStatus.Profit);
            }
            else
            {
                var lossAbs = lossAmounts.Length == 0
                    ? cfg.DefaultTradeAmount
                    : lossAmounts[Math.Min(lossIdx, lossAmounts.Length - 1)];
                amount = lossAbs > 0 ? lossAbs : cfg.DefaultTradeAmount;
                pnl = -amount;
                lossIdx++;
                status = nameof(TradeStatus.Loss);
            }

            if (amount < 1m) amount = cfg.DefaultTradeAmount;

            var created = now.AddMinutes(-(i * 17 + StableInt(Hash(userId) + i, 11)));
            var asset = Assets[StableInt(Hash(userId) + i * 17, Assets.Length)];
            var call = StableBool(userId, i, 0.52);

            list.Add(new TradeDto(
                Id: DemoGuid(userId, "hist", i).ToString(),
                BinollaOrderId: $"mkt-demo-h-{i}",
                Asset: asset,
                Direction: call ? "CALL" : "PUT",
                Amount: Math.Round(amount, 2),
                DurationSeconds: 60,
                Status: status,
                Pnl: Math.Round(pnl, 2),
                ErrorCode: null,
                CreatedAt: created,
                UpdatedAt: created.AddSeconds(60)));
        }

        return list.OrderByDescending(t => t.CreatedAt).ToList();
    }

    private static decimal[] SplitTotal(decimal total, int parts, Guid userId, int salt)
    {
        if (parts <= 0)
            return [];
        if (parts == 1)
            return [Math.Round(Math.Max(0m, total), 2)];

        var weights = new double[parts];
        var sumW = 0.0;
        for (var i = 0; i < parts; i++)
        {
            weights[i] = 0.55 + StableInt(Hash(userId) ^ (salt * 97 + i * 13), 100) / 100.0;
            sumW += weights[i];
        }

        var result = new decimal[parts];
        decimal allocated = 0;
        for (var i = 0; i < parts - 1; i++)
        {
            var share = Math.Round(total * (decimal)(weights[i] / sumW), 2);
            result[i] = share;
            allocated += share;
        }

        result[parts - 1] = Math.Round(Math.Max(0m, total - allocated), 2);
        return result;
    }

    private List<NotificationDto> BuildNotificationItems(Guid userId)
    {
        var readBag = ReadNotifications.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, bool>());
        var now = DateTimeOffset.UtcNow;
        var trades = BuildHistoryFromConfig(userId).Take(8).ToList();
        var items = new List<NotificationDto>();

        items.Add(MakeNotification(
            userId, "bot", 1, "bot-started", "Bot started",
            "Alpha Momentum · EUR/USD · 1m", now.AddMinutes(-3), "/bot", readBag));

        items.Add(MakeNotification(
            userId, "signal", 1, "new-signal", "New signal detected",
            "CALL on EURUSD_otc (strength 82%).", now.AddMinutes(-8), "/trading", readBag));

        for (var i = 0; i < Math.Min(6, trades.Count); i++)
        {
            var trade = trades[i];
            var variant = trade.Status switch
            {
                nameof(TradeStatus.Running) => "live-trade",
                nameof(TradeStatus.Profit) => "trade-profit",
                nameof(TradeStatus.Loss) => "trade-loss",
                _ => "live-trade"
            };
            var title = trade.Status switch
            {
                nameof(TradeStatus.Running) => "Live trade",
                nameof(TradeStatus.Profit) => "Trade profit",
                nameof(TradeStatus.Loss) => "Trade loss",
                _ => "Trade update"
            };
            var desc = trade.Pnl is null
                ? $"{trade.Amount:0.##}$ {trade.Direction} on {trade.Asset}"
                : $"{(trade.Pnl >= 0 ? "+" : "")}{trade.Pnl:0.##}$ on {trade.Asset}";
            items.Add(MakeNotification(
                userId, "trade", i + 1, variant, title, desc, trade.CreatedAt,
                $"/trading/{trade.Id}", readBag, trade.Id));
        }

        var cfg = GetConfig(userId);
        items.Add(MakeNotification(
            userId, "target", 1, "profit-target", "Profit target reached",
            $"Configured demo profit +${cfg.TotalProfit:0.##} story line.", now.AddHours(-5), "/bot", readBag));

        return items.OrderByDescending(n => n.CreatedAt).ToList();
    }

    private static NotificationDto MakeNotification(
        Guid userId,
        string kind,
        int index,
        string variant,
        string title,
        string description,
        DateTimeOffset createdAt,
        string actionPath,
        ConcurrentDictionary<Guid, bool> readBag,
        string? tradeId = null)
    {
        var id = DemoGuid(userId, $"notif-{kind}", index);
        var read = readBag.ContainsKey(id);
        return new NotificationDto(
            Id: id.ToString(),
            Variant: variant,
            Title: title,
            Description: description,
            Read: read,
            TradeId: tradeId,
            ActionPath: actionPath,
            CreatedAt: createdAt);
    }

    private decimal LiveBalance(Guid userId)
    {
        var cfg = GetConfig(userId);
        var tick = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 12;
        var seed = Hash(userId);
        var wobble = cfg.BalanceWobble <= 0
            ? 0m
            : (decimal)(Math.Sin(tick * 0.35 + seed) * (double)cfg.BalanceWobble
                        + Math.Cos(tick * 0.11) * (double)(cfg.BalanceWobble * 0.32m));
        return Math.Round(cfg.Balance + wobble, 2);
    }

    private static decimal BasePrice(string symbol)
    {
        var h = Hash(symbol);
        return symbol.ToUpperInvariant() switch
        {
            var s when s.Contains("JPY", StringComparison.Ordinal) => 148.25m + (h % 100) / 100m,
            var s when s.Contains("GBP", StringComparison.Ordinal) => 1.265m + (h % 80) / 10000m,
            _ => 1.085m + (h % 120) / 10000m
        };
    }

    private static Guid DemoGuid(Guid userId, string kind, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{kind}:{index}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static int Hash(Guid id)
    {
        var b = id.ToByteArray();
        return BitConverter.ToInt32(b, 0) ^ BitConverter.ToInt32(b, 4);
    }

    private static int Hash(string value)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in value)
                h = h * 31 + c;
            return h;
        }
    }

    private static int StableInt(int seed, int modulo)
    {
        if (modulo <= 0) return 0;
        var v = seed % modulo;
        return v < 0 ? v + modulo : v;
    }

    private static bool StableBool(Guid userId, long salt, double trueProbability)
    {
        var sample = StableInt(Hash(userId) ^ (int)(salt & 0x7fffffff), 10_000) / 10_000.0;
        return sample < trueProbability;
    }
}
