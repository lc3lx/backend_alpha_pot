using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Binolla.Abstractions;
using ScarAlpha.Binolla.Models;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;
using EngineDirection = ScarAlpha.Binolla.Models.TradeDirection;
using DomainDirection = ScarAlpha.Domain.Enums.TradeDirection;

namespace ScarAlpha.Application.Services;

public sealed class TradeAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly ITradeRepository _trades;
    private readonly IBinollaLinkRepository _links;
    private readonly IBinollaSessionManager _sessions;
    private readonly ITradeOutcomeWorker _outcomeWorker;
    private readonly IIdempotencyGate _idempotencyGate;
    private readonly IBotAccessService _botAccess;
    private readonly StrategyAppService _strategies;
    private readonly INotificationWriter _notifications;
    private readonly IMarketingDemoService _demo;
    private readonly ILogger<TradeAppService> _logger;

    public TradeAppService(
        ICurrentUser currentUser,
        ITradeRepository trades,
        IBinollaLinkRepository links,
        IBinollaSessionManager sessions,
        ITradeOutcomeWorker outcomeWorker,
        IIdempotencyGate idempotencyGate,
        IBotAccessService botAccess,
        StrategyAppService strategies,
        INotificationWriter notifications,
        IMarketingDemoService demo,
        ILogger<TradeAppService> logger)
    {
        _currentUser = currentUser;
        _trades = trades;
        _links = links;
        _sessions = sessions;
        _outcomeWorker = outcomeWorker;
        _idempotencyGate = idempotencyGate;
        _botAccess = botAccess;
        _strategies = strategies;
        _notifications = notifications;
        _demo = demo;
        _logger = logger;
    }

    public async Task<TradeDto> PlaceTradeAsync(PlaceTradeRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ApiException(ApiErrorCodes.ValidationError, "Idempotency-Key header is required.");

        ValidateTradeRequest(request);
        _strategies.EnsureStrategyEnabled(request.StrategyId);

        var userId = _currentUser.UserId;
        if (await _demo.IsMarketingDemoAsync(userId, ct))
        {
            // Simulated only — never touches Binolla.
            return _demo.PlaceSimulatedTrade(userId, request, idempotencyKey.Trim());
        }

        var key = idempotencyKey.Trim();

        await using var gate = await _idempotencyGate.AcquireAsync(userId, key, ct);

        var existing = await _trades.GetByIdempotencyKeyAsync(userId, key, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Idempotency duplicate for user {UserId} key={Key} tradeId={TradeId}",
                userId, key, existing.Id);
            return Map(existing);
        }

        var access = await _botAccess.CheckAsync(userId, ct);
        AccountAppService.EnsureAllowed(access);

        var link = await _links.GetByUserIdAsync(userId, ct);
        if (link is null || link.Status != BinollaLinkStatus.Connected)
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Connect Binolla before trading.", 409);

        if (link.AccountType != BinollaAccountType.Demo)
            throw new ApiException(ApiErrorCodes.RealTradingDisabled, "Real trading is disabled in this phase.", 403);

        var client = _sessions.Get(userId.ToString());
        if (client is null ||
            client.Lifecycle is not (SessionLifecycleState.Connected or SessionLifecycleState.Reconnected))
        {
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Binolla session is not connected.", 409);
        }

        // Authoritative balance check from live Binolla session (not a local wallet).
        try
        {
            var balance = await client.GetBalanceAsync(ct);
            if (balance.CurrentType != AccountType.Demo)
                throw new ApiException(ApiErrorCodes.RealTradingDisabled, "Real trading is disabled in this phase.", 403);
            if (balance.CurrentBalance < request.Amount)
                throw new ApiException(ApiErrorCodes.InsufficientBalance, "Insufficient Demo balance.", 400);
        }
        catch (ApiException) { throw; }
        catch (BinollaAuthenticationException)
        {
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Balance check failed for user {UserId}", userId);
            throw new ApiException(ApiErrorCodes.BinollaNotConnected, "Unable to verify Binolla balance.", 409);
        }

        var direction = ParseDirection(request.Direction);
        var now = DateTimeOffset.UtcNow;
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Asset = request.Asset.Trim(),
            Direction = direction,
            Amount = request.Amount,
            DurationSeconds = request.DurationSeconds,
            Status = TradeStatus.Pending,
            IdempotencyKey = key,
            CreatedAt = now,
            UpdatedAt = now
        };

        _logger.LogInformation(
            "Trade requested user={UserId} tradeId={TradeId} asset={Asset} amount={Amount}",
            userId, trade.Id, trade.Asset, trade.Amount);

        try
        {
            await _trades.AddAsync(trade, ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            var again = await _trades.GetByIdempotencyKeyAsync(userId, key, ct)
                        ?? throw new ApiException(ApiErrorCodes.DuplicateRequest, "Duplicate trade request.", 409);
            _logger.LogInformation(
                "Idempotency race resolved user={UserId} key={Key} tradeId={TradeId}",
                userId, key, again.Id);
            return Map(again);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var order = await client.PlaceOrderAsync(
                trade.Asset,
                direction == DomainDirection.Call ? EngineDirection.Call : EngineDirection.Put,
                trade.Amount,
                trade.DurationSeconds,
                ct);

            var status = trade.Status;
            if (!TradeStateMachine.TryTransition(ref status, TradeStatus.Running))
                throw new InvalidOperationException("Unable to transition trade to Running.");

            trade.Status = status;
            trade.BinollaOrderId = order.OrderId;
            trade.UpdatedAt = DateTimeOffset.UtcNow;
            await _trades.UpdateAsync(trade, ct);

            _outcomeWorker.Enqueue(trade.Id, userId, order.OrderId);
            _logger.LogInformation(
                "Trade accepted user={UserId} tradeId={TradeId} binollaOrderId={BinollaOrderId} placeMs={ElapsedMs}",
                userId, trade.Id, order.OrderId, sw.ElapsedMilliseconds);

            await _notifications.AddAsync(
                userId,
                "live-trade",
                "Live trade",
                $"{trade.Asset} {trade.Direction} {trade.Amount:0.##} opened.",
                trade.Id,
                $"/trading/{trade.Id}",
                ct);

            return Map(trade);
        }
        catch (BinollaOrderException ex)
        {
            await FailTradeAsync(trade, MapOrderError(ex.Message), ct);
            throw new ApiException(trade.ErrorCode!, "Trade could not be opened.", 400);
        }
        catch (BinollaAuthenticationException)
        {
            await FailTradeAsync(trade, ApiErrorCodes.BinollaSessionExpired, ct);
            throw new ApiException(ApiErrorCodes.BinollaSessionExpired, "Binolla session expired.", 401);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trade placement failed for user {UserId} tradeId={TradeId}", userId, trade.Id);
            await FailTradeAsync(trade, ApiErrorCodes.BinollaConnectionFailed, ct);
            throw new ApiException(ApiErrorCodes.BinollaConnectionFailed, "Unable to place trade.", 502);
        }
    }

    public async Task<TradeListResponse> ListTradesAsync(
        int page,
        int pageSize,
        string? status,
        string? asset,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var userId = _currentUser.UserId;
        if (await _demo.IsMarketingDemoAsync(userId, ct))
            return _demo.BuildTrades(userId, page, pageSize, status, asset);

        TradeStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TradeStatus>(status, ignoreCase: true, out var s))
                throw new ApiException(ApiErrorCodes.ValidationError, "Invalid status filter.");
            parsedStatus = s;
        }

        var total = await _trades.CountByUserAsync(userId, parsedStatus, asset, ct);
        var trades = await _trades.ListByUserAsync(userId, pageSize, (page - 1) * pageSize, parsedStatus, asset, ct);
        return new TradeListResponse(trades.Select(Map).ToList(), total, page, pageSize);
    }

    public async Task<TradeDto> GetTradeAsync(Guid tradeId, CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
        {
            return _demo.FindTrade(_currentUser.UserId, tradeId)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Trade not found.", 404);
        }

        var trade = await _trades.GetByIdAsync(tradeId, _currentUser.UserId, ct)
                    ?? throw new ApiException(ApiErrorCodes.NotFound, "Trade not found.", 404);
        return Map(trade);
    }

    private async Task FailTradeAsync(Trade trade, string errorCode, CancellationToken ct)
    {
        var status = trade.Status;
        if (TradeStateMachine.TryTransition(ref status, TradeStatus.Failed))
        {
            trade.Status = status;
            trade.ErrorCode = errorCode;
            trade.UpdatedAt = DateTimeOffset.UtcNow;
            await _trades.UpdateAsync(trade, ct);
            _logger.LogInformation(
                "Trade failed user={UserId} tradeId={TradeId} code={Code}",
                trade.UserId, trade.Id, errorCode);
        }
    }

    private static void ValidateTradeRequest(PlaceTradeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Asset))
            throw new ApiException(ApiErrorCodes.InvalidTrade, "asset is required.");
        if (request.Amount <= 0)
            throw new ApiException(ApiErrorCodes.InvalidTrade, "amount must be greater than zero.");
        if (request.Amount > 100_000m)
            throw new ApiException(ApiErrorCodes.InvalidTrade, "amount exceeds maximum allowed for Demo trades.");
        if (request.DurationSeconds is < 5 or > 3600)
            throw new ApiException(ApiErrorCodes.InvalidTrade, "durationSeconds must be between 5 and 3600.");
        if (string.IsNullOrWhiteSpace(request.StrategyId))
            throw new ApiException(ApiErrorCodes.ValidationError, "strategyId is required.");
        _ = ParseDirection(request.Direction);
    }

    private static DomainDirection ParseDirection(string? direction)
    {
        return (direction ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "CALL" or "UP" => DomainDirection.Call,
            "PUT" or "DOWN" => DomainDirection.Put,
            _ => throw new ApiException(ApiErrorCodes.InvalidTrade, "direction must be CALL or PUT.")
        };
    }

    private static string MapOrderError(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("balance")) return ApiErrorCodes.InsufficientBalance;
        if (m.Contains("market") || m.Contains("asset")) return ApiErrorCodes.BinollaMarketUnavailable;
        return ApiErrorCodes.InvalidTrade;
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("IX_Trades_UserId_IdempotencyKey", StringComparison.OrdinalIgnoreCase)
               || text.Contains("IX_trades_UserId_IdempotencyKey", StringComparison.OrdinalIgnoreCase)
               || text.Contains("23505", StringComparison.Ordinal); // PostgreSQL unique_violation
    }

    private static TradeDto Map(Trade trade) => new(
        trade.Id.ToString(),
        trade.BinollaOrderId,
        trade.Asset,
        trade.Direction.ToString().ToUpperInvariant(),
        trade.Amount,
        trade.DurationSeconds,
        trade.Status.ToString(),
        trade.Pnl,
        trade.ErrorCode,
        trade.CreatedAt,
        trade.UpdatedAt);
}
