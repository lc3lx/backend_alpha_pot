using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class AdminAppService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ApprovalGates = new();

    private readonly ICurrentUser _currentUser;
    private readonly IBinollaLinkRepository _links;
    private readonly IUserRepository _users;
    private readonly IUserPasswordHasher _passwords;
    private readonly IAuditService _audit;
    private readonly INotificationWriter _notifications;
    private readonly INotificationRepository _notificationRepo;
    private readonly IBotRuntimeService _botRuntime;
    private readonly IBotAccessService _botAccess;
    private readonly ITradeRepository _trades;
    private readonly ILogger<AdminAppService> _logger;

    public AdminAppService(
        ICurrentUser currentUser,
        IBinollaLinkRepository links,
        IUserRepository users,
        IUserPasswordHasher passwords,
        IAuditService audit,
        INotificationWriter notifications,
        INotificationRepository notificationRepo,
        IBotRuntimeService botRuntime,
        IBotAccessService botAccess,
        ITradeRepository trades,
        ILogger<AdminAppService> logger)
    {
        _currentUser = currentUser;
        _links = links;
        _users = users;
        _passwords = passwords;
        _audit = audit;
        _notifications = notifications;
        _notificationRepo = notificationRepo;
        _botRuntime = botRuntime;
        _botAccess = botAccess;
        _trades = trades;
        _logger = logger;
    }

    public async Task<AdminBinollaAccountListResponse> ListAsync(
        string? status,
        CancellationToken ct,
        string? q = null,
        int page = 1,
        int pageSize = 50)
    {
        await EnsureAdminAsync(ct);
        AdminApprovalStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AdminApprovalStatus>(status, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "status must be Pending, Approved, or Rejected.");
            filter = parsed;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (links, total) = await _links.SearchAsync(filter, q, page, pageSize, ct);
        var items = new List<AdminBinollaAccountDto>();
        foreach (var link in links)
        {
            var user = await _users.GetByIdAsync(link.UserId, ct);
            if (user is null) continue;
            items.Add(Map(link, user));
        }

        return new AdminBinollaAccountListResponse(items, total, page, pageSize);
    }

    public async Task<AdminBinollaAccountDto> GetAsync(Guid id, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var link = await _links.GetByIdAsync(id, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Binolla link not found.", 404);
        var user = await _users.GetByIdAsync(link.UserId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);
        return Map(link, user);
    }

    public async Task<AdminBinollaAccountDto> ApproveAsync(Guid id, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var gate = ApprovalGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var link = await _links.GetByIdAsync(id, ct)
                       ?? throw new ApiException(ApiErrorCodes.NotFound, "Binolla link not found.", 404);
            var user = await _users.GetByIdAsync(link.UserId, ct)
                       ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

            var previous = $"{link.ApprovalStatus}:{link.AdminApproved}";
            if (link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Approved)
                return Map(link, user);

            ApplyApprovalState(link, AdminApprovalStatus.Approved, approved: true);
            await _links.UpsertAsync(link, ct);

            await _audit.RecordAsync(
                action: "BinollaAccountApproved",
                actorUserId: _currentUser.UserId,
                targetUserId: link.UserId,
                targetBinollaLinkId: link.Id,
                previousState: previous,
                newState: $"{link.ApprovalStatus}:{link.AdminApproved}",
                detail: $"ApprovedBy={link.ApprovedBy}",
                ct: ct);

            _logger.LogInformation(
                "Admin approved binolla link={LinkId} user={UserId} by={Admin}",
                link.Id, link.UserId, link.ApprovedBy);

            await _notifications.AddAsync(
                link.UserId,
                "account-approved",
                "Account approved",
                "An administrator approved your Binolla account. Demo trading is now available.",
                actionPath: "/trading",
                ct: ct);

            return Map(link, user);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AdminBinollaAccountDto> RejectAsync(Guid id, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var gate = ApprovalGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var link = await _links.GetByIdAsync(id, ct)
                       ?? throw new ApiException(ApiErrorCodes.NotFound, "Binolla link not found.", 404);
            var user = await _users.GetByIdAsync(link.UserId, ct)
                       ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

            var previous = $"{link.ApprovalStatus}:{link.AdminApproved}";
            if (!link.AdminApproved && link.ApprovalStatus == AdminApprovalStatus.Rejected)
                return Map(link, user);

            ApplyApprovalState(link, AdminApprovalStatus.Rejected, approved: false);
            await _links.UpsertAsync(link, ct);

            await _audit.RecordAsync(
                action: "BinollaAccountRejected",
                actorUserId: _currentUser.UserId,
                targetUserId: link.UserId,
                targetBinollaLinkId: link.Id,
                previousState: previous,
                newState: $"{link.ApprovalStatus}:{link.AdminApproved}",
                detail: $"RejectedBy={link.ApprovedBy}",
                ct: ct);

            _logger.LogInformation(
                "Admin rejected binolla link={LinkId} user={UserId} by={Admin}",
                link.Id, link.UserId, link.ApprovedBy);

            await _notifications.AddAsync(
                link.UserId,
                "account-not-approved",
                "Account not approved",
                "An administrator rejected this Binolla account. Contact support if this looks wrong.",
                actionPath: "/settings",
                ct: ct);

            return Map(link, user);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MarketingDemoUserListResponse> ListMarketingDemoUsersAsync(
        CancellationToken ct,
        bool? active = true,
        int page = 1,
        int pageSize = 50)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var users = await _users.ListMarketingDemoUsersAsync(active, ct);
        var total = users.Count;
        var items = users
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapDemo)
            .ToList();
        return new MarketingDemoUserListResponse(items, total, page, pageSize);
    }

    public async Task<AdminUserListResponse> ListUsersAsync(
        string? q,
        string? role,
        bool? isMarketingDemo,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        UserRole? roleFilter = null;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "role must be User or Admin.");
            roleFilter = parsed;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (users, total) = await _users.SearchAsync(q, roleFilter, isMarketingDemo, page, pageSize, ct);
        var items = new List<AdminUserListItemDto>();
        foreach (var user in users)
        {
            var link = await _links.GetByUserIdAsync(user.Id, ct);
            items.Add(new AdminUserListItemDto(
                Id: user.Id.ToString(),
                Email: user.Email,
                FullName: user.FullName,
                Username: user.Username,
                TelegramUserId: user.TelegramUserId,
                Role: user.Role.ToString(),
                IsMarketingDemo: user.IsMarketingDemo,
                BinollaApprovalStatus: link?.ApprovalStatus.ToString(),
                BinollaConnected: link is not null && link.Status == BinollaLinkStatus.Connected,
                CreatedAt: user.CreatedAt,
                UpdatedAt: user.UpdatedAt));
        }

        return new AdminUserListResponse(items, total, page, pageSize);
    }

    public async Task<AdminUserDetailDto> GetUserAsync(Guid userId, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);
        var link = await _links.GetByUserIdAsync(user.Id, ct);
        return new AdminUserDetailDto(
            Id: user.Id.ToString(),
            Email: user.Email,
            FullName: user.FullName,
            Username: user.Username,
            Country: user.Country,
            TelegramUserId: user.TelegramUserId,
            Role: user.Role.ToString(),
            IsAdmin: user.Role == UserRole.Admin,
            IsMarketingDemo: user.IsMarketingDemo,
            MarketingConfig: user.IsMarketingDemo || user.MarketingDemoConfigJson != null
                ? MarketingDemoConfigStore.FromUser(user)
                : null,
            BinollaAccount: link is null ? null : Map(link, user),
            CreatedAt: user.CreatedAt,
            UpdatedAt: user.UpdatedAt);
    }

    public async Task<AdminUserDetailDto> PatchUserAsync(
        Guid userId,
        PatchAdminUserRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");

        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

        if (request.IsMarketingDemo == true && user.Role == UserRole.Admin)
            throw new ApiException(ApiErrorCodes.ValidationError, "Admin accounts cannot be marketing demos.");

        var previousDemo = user.IsMarketingDemo.ToString();

        if (request.ClearTelegramUserId)
        {
            user.TelegramUserId = null;
        }
        else if (request.TelegramUserId is not null)
        {
            var tg = NormalizeTelegramUserId(request.TelegramUserId)
                     ?? throw new ApiException(ApiErrorCodes.ValidationError, "Telegram user id must be a positive number.");
            await AttachTelegramUserIdAsync(user, tg, ct);
        }

        if (request.IsMarketingDemo is bool demoFlag)
        {
            user.IsMarketingDemo = demoFlag;
            if (demoFlag && string.IsNullOrWhiteSpace(user.MarketingDemoConfigJson))
                MarketingDemoConfigStore.ApplyToUser(user, request.Config ?? MarketingDemoConfigStore.Default);
        }

        if (request.Config is not null)
            MarketingDemoConfigStore.ApplyToUser(user, request.Config);

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);

        if (request.IsMarketingDemo is bool changed && changed.ToString() != previousDemo)
        {
            await _audit.RecordAsync(
                action: changed ? "MarketingDemoEnabled" : "MarketingDemoDisabled",
                actorUserId: _currentUser.UserId,
                targetUserId: user.Id,
                targetBinollaLinkId: null,
                previousState: previousDemo,
                newState: user.IsMarketingDemo.ToString(),
                detail: "via PATCH /api/admin/users",
                ct: ct);
        }

        return await GetUserAsync(user.Id, ct);
    }

    public async Task<AdminAuditListResponse> ListAuditAsync(
        Guid? userId,
        string? action,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (events, total) = await _audit.SearchAsync(userId, action, from, to, page, pageSize, ct);
        var items = events.Select(e => new AdminAuditEventDto(
            Id: e.Id.ToString(),
            Action: e.Action,
            ActorUserId: e.ActorUserId.ToString(),
            TargetUserId: e.TargetUserId?.ToString(),
            TargetBinollaLinkId: e.TargetBinollaLinkId?.ToString(),
            PreviousState: e.PreviousState,
            NewState: e.NewState,
            Detail: e.Detail,
            CreatedAt: e.CreatedAt)).ToList();
        return new AdminAuditListResponse(items, total, page, pageSize);
    }

    public async Task<AdminSendNotificationResponse> SendNotificationsAsync(
        AdminSendNotificationRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");

        var title = (request.Title ?? string.Empty).Trim();
        var description = (request.Description ?? string.Empty).Trim();
        if (title.Length is < 1 or > 200)
            throw new ApiException(ApiErrorCodes.ValidationError, "Title must be 1–200 characters.");
        if (description.Length is < 1 or > 2000)
            throw new ApiException(ApiErrorCodes.ValidationError, "Description must be 1–2000 characters.");

        var variant = string.IsNullOrWhiteSpace(request.Variant) ? "admin-message" : request.Variant.Trim();
        var actionPath = string.IsNullOrWhiteSpace(request.ActionPath) ? null : request.ActionPath.Trim();

        var targetIds = new HashSet<Guid>();
        if (request.UserIds is { Count: > 0 })
        {
            foreach (var raw in request.UserIds)
            {
                if (!Guid.TryParse(raw, out var id))
                    throw new ApiException(ApiErrorCodes.ValidationError, $"Invalid user id: {raw}");
                targetIds.Add(id);
            }
        }

        if (request.AllApprovedUsers)
        {
            var (approvedLinks, _) = await _links.SearchAsync(AdminApprovalStatus.Approved, null, 1, 10_000, ct);
            foreach (var link in approvedLinks)
                targetIds.Add(link.UserId);
        }

        if (targetIds.Count == 0)
            throw new ApiException(ApiErrorCodes.ValidationError, "Select at least one recipient (userIds or allApprovedUsers).");

        if (targetIds.Count > 500)
            throw new ApiException(ApiErrorCodes.ValidationError, "Cannot send to more than 500 users in one request.");

        var sentIds = new List<string>();
        foreach (var userId in targetIds)
        {
            var user = await _users.GetByIdAsync(userId, ct);
            if (user is null) continue;

            await _notifications.AddAsync(userId, variant, title, description, actionPath: actionPath, ct: ct);
            sentIds.Add(userId.ToString());
        }

        await _audit.RecordAsync(
            action: "AdminNotificationSent",
            actorUserId: _currentUser.UserId,
            targetUserId: null,
            targetBinollaLinkId: null,
            previousState: null,
            newState: $"sent={sentIds.Count}",
            detail: $"title={title};allApproved={request.AllApprovedUsers}",
            ct: ct);

        return new AdminSendNotificationResponse(sentIds.Count, sentIds);
    }

    public async Task<AdminNotificationListResponse> ListNotificationsAsync(
        Guid? userId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (rows, total) = await _notificationRepo.SearchAdminAsync(userId, page, pageSize, ct);
        var items = rows.Select(n => new AdminNotificationDto(
            Id: n.Id.ToString(),
            UserId: n.UserId.ToString(),
            Variant: n.Variant,
            Title: n.Title,
            Description: n.Description,
            Read: n.Read,
            ActionPath: n.ActionPath,
            CreatedAt: n.CreatedAt)).ToList();
        return new AdminNotificationListResponse(items, total, page, pageSize);
    }

    public async Task<AdminBotListResponse> ListBotsAsync(
        string? state,
        string? q,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        BotRunState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<BotRunState>(state, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "state must be Stopped, Running, or Paused.");
            stateFilter = parsed;
        }

        var known = _botRuntime.ListKnown().ToDictionary(x => x.UserId);
        var (users, _) = await _users.SearchAsync(q, role: null, isMarketingDemo: null, page: 1, pageSize: 500, ct);
        var userMap = users.ToDictionary(x => x.Id);

        foreach (var runtime in known.Values)
        {
            if (userMap.ContainsKey(runtime.UserId)) continue;
            var u = await _users.GetByIdAsync(runtime.UserId, ct);
            if (u is not null) userMap[u.Id] = u;
        }

        var rows = new List<AdminBotRuntimeDto>();
        foreach (var user in userMap.Values)
        {
            var runtime = known.TryGetValue(user.Id, out var r) ? r : _botRuntime.Get(user.Id);
            if (stateFilter is BotRunState sf && runtime.State != sf)
                continue;

            var access = await _botAccess.CheckAsync(user.Id, ct);
            rows.Add(new AdminBotRuntimeDto(
                UserId: user.Id.ToString(),
                Email: user.Email,
                FullName: user.FullName,
                TelegramUserId: user.TelegramUserId,
                BotAccess: access.Access.ToString(),
                State: runtime.State.ToString(),
                Asset: runtime.Asset,
                Amount: runtime.Amount,
                DurationSeconds: runtime.DurationSeconds,
                DailyProfitTarget: runtime.DailyProfitTarget,
                DailyLossLimit: runtime.DailyLossLimit,
                UpdatedAt: runtime.UpdatedAt,
                IsMarketingDemo: user.IsMarketingDemo,
                Assets: runtime.ResolvedAssets));
        }

        rows = rows
            .OrderByDescending(x => x.State == "Running")
            .ThenByDescending(x => x.UpdatedAt)
            .ToList();

        var total = rows.Count;
        var pageItems = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new AdminBotListResponse(pageItems, total, page, pageSize);
    }

    public async Task<AdminBotRuntimeDto> GetBotAsync(Guid userId, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);
        var runtime = _botRuntime.Get(userId);
        var access = await _botAccess.CheckAsync(userId, ct);
        return new AdminBotRuntimeDto(
            UserId: user.Id.ToString(),
            Email: user.Email,
            FullName: user.FullName,
            TelegramUserId: user.TelegramUserId,
            BotAccess: access.Access.ToString(),
            State: runtime.State.ToString(),
            Asset: runtime.Asset,
            Amount: runtime.Amount,
            DurationSeconds: runtime.DurationSeconds,
            DailyProfitTarget: runtime.DailyProfitTarget,
            DailyLossLimit: runtime.DailyLossLimit,
            UpdatedAt: runtime.UpdatedAt,
            IsMarketingDemo: user.IsMarketingDemo,
            Assets: runtime.ResolvedAssets);
    }

    public async Task<AdminBotRuntimeDto> ControlBotAsync(
        Guid userId,
        AdminBotControlRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Action))
            throw new ApiException(ApiErrorCodes.ValidationError, "action is required (start|pause|stop|apply).");

        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

        var action = request.Action.Trim().ToLowerInvariant();
        var previous = _botRuntime.Get(userId).State.ToString();
        BotRuntimeConfig next;

        switch (action)
        {
            case "start":
            {
                var assets = BotAssetList.Normalize(request.Asset, request.Assets);
                if (assets.Count == 0)
                    throw new ApiException(ApiErrorCodes.ValidationError, "Select at least one trading pair to start the bot.");
                var access = await _botAccess.CheckAsync(userId, ct);
                if (access.Access != BotAccessState.Allowed && !user.IsMarketingDemo)
                    throw new ApiException(ApiErrorCodes.Forbidden, $"User bot access is {access.Access}.", 403);
                next = _botRuntime.Start(
                    userId,
                    assets,
                    request.Amount ?? 25m,
                    request.DurationSeconds ?? 300,
                    request.DailyProfitTarget ?? 50m,
                    request.DailyLossLimit ?? 30m);
                break;
            }
            case "pause":
                next = _botRuntime.Pause(userId);
                break;
            case "stop":
                next = _botRuntime.Stop(userId);
                break;
            case "apply":
                next = _botRuntime.Apply(
                    userId,
                    request.Asset,
                    request.Amount,
                    request.DurationSeconds,
                    request.DailyProfitTarget,
                    request.DailyLossLimit,
                    assets: request.Assets);
                break;
            default:
                throw new ApiException(ApiErrorCodes.ValidationError, "action must be start, pause, stop, or apply.");
        }

        await _audit.RecordAsync(
            action: $"AdminBot_{action}",
            actorUserId: _currentUser.UserId,
            targetUserId: userId,
            targetBinollaLinkId: null,
            previousState: previous,
            newState: next.State.ToString(),
            detail: $"asset={next.Asset};amount={next.Amount}",
            ct: ct);

        return await GetBotAsync(userId, ct);
    }

    public async Task<AdminTradeListResponse> ListTradesAsync(
        Guid? userId,
        string? status,
        string? asset,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        TradeStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TradeStatus>(status, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "Invalid trade status.");
            statusFilter = parsed;
        }

        var (trades, total) = await _trades.SearchAdminAsync(userId, statusFilter, asset, page, pageSize, ct);
        var items = new List<AdminTradeDto>();
        foreach (var trade in trades)
        {
            var tradeUser = await _users.GetByIdAsync(trade.UserId, ct);
            items.Add(new AdminTradeDto(
                Id: trade.Id.ToString(),
                UserId: trade.UserId.ToString(),
                Email: tradeUser?.Email,
                FullName: tradeUser?.FullName,
                Asset: trade.Asset,
                Direction: trade.Direction.ToString(),
                Amount: trade.Amount,
                Status: trade.Status.ToString(),
                Pnl: trade.Pnl,
                CreatedAt: trade.CreatedAt,
                ClosedAt: trade.Status is TradeStatus.Pending or TradeStatus.Running
                    ? null
                    : trade.UpdatedAt));
        }

        return new AdminTradeListResponse(items, total, page, pageSize);
    }

    public async Task<MarketingDemoUserDto> CreateMarketingDemoUserAsync(
        CreateMarketingDemoUserRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");

        var telegramUserId = NormalizeTelegramUserId(request.TelegramUserId);
        var hasEmail = !string.IsNullOrWhiteSpace(request.Email);
        if (!hasEmail && telegramUserId is null)
        {
            throw new ApiException(
                ApiErrorCodes.ValidationError,
                "Provide an email/password and/or a Telegram user id for the marketing demo.");
        }

        string? email = null;
        if (hasEmail)
        {
            email = NormalizeEmail(request.Email);
            ValidatePassword(request.Password);
        }

        // Prefer promoting an existing Telegram identity (bot-first marketing demos).
        if (telegramUserId is long tgId)
        {
            var byTelegram = await _users.GetByTelegramUserIdAsync(tgId, ct);
            if (byTelegram is not null)
            {
                if (byTelegram.Role == UserRole.Admin)
                    throw new ApiException(ApiErrorCodes.ValidationError, "Admin accounts cannot be marketing demos.");

                if (email is not null)
                {
                    var emailOwner = await _users.GetByEmailAsync(email, ct);
                    if (emailOwner is not null && emailOwner.Id != byTelegram.Id)
                    {
                        throw new ApiException(
                            ApiErrorCodes.EmailTaken,
                            "That email belongs to a different account.",
                            409);
                    }

                    byTelegram.Email = email;
                    byTelegram.PasswordHash = _passwords.Hash(request.Password!);
                }

                ApplyDemoProfile(byTelegram, request);
                byTelegram.IsMarketingDemo = true;
                MarketingDemoConfigStore.ApplyToUser(byTelegram, request.Config);
                byTelegram.UpdatedAt = DateTimeOffset.UtcNow;
                await _users.UpdateAsync(byTelegram, ct);

                await _audit.RecordAsync(
                    action: "MarketingDemoEnabled",
                    actorUserId: _currentUser.UserId,
                    targetUserId: byTelegram.Id,
                    targetBinollaLinkId: null,
                    previousState: "False",
                    newState: "True",
                    detail: $"telegram_user_id={tgId};email={byTelegram.Email}",
                    ct: ct);

                _logger.LogInformation(
                    "Admin promoted telegram user {TelegramUserId} → marketing demo {UserId}",
                    tgId, byTelegram.Id);
                return MapDemo(byTelegram);
            }
        }

        if (email is not null)
        {
            var existing = await _users.GetByEmailAsync(email, ct);
            if (existing is not null)
            {
                if (existing.IsMarketingDemo && telegramUserId is null)
                {
                    throw new ApiException(
                        ApiErrorCodes.EmailTaken,
                        "A marketing demo account with this email already exists.",
                        409);
                }

                if (existing.Role == UserRole.Admin)
                    throw new ApiException(ApiErrorCodes.ValidationError, "Admin accounts cannot be marketing demos.");

                if (telegramUserId is long attachTg)
                    await AttachTelegramUserIdAsync(existing, attachTg, ct);

                existing.IsMarketingDemo = true;
                existing.PasswordHash = _passwords.Hash(request.Password!);
                ApplyDemoProfile(existing, request);
                MarketingDemoConfigStore.ApplyToUser(existing, request.Config);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _users.UpdateAsync(existing, ct);

                await _audit.RecordAsync(
                    action: "MarketingDemoEnabled",
                    actorUserId: _currentUser.UserId,
                    targetUserId: existing.Id,
                    targetBinollaLinkId: null,
                    previousState: "False",
                    newState: "True",
                    detail: $"email={email};telegram_user_id={existing.TelegramUserId}",
                    ct: ct);

                _logger.LogInformation("Admin promoted user {UserId} to marketing demo", existing.Id);
                return MapDemo(existing);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = email is null ? null : _passwords.Hash(request.Password!),
            TelegramUserId = null,
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? "Marketing Demo" : request.FullName.Trim(),
            Username = NormalizeUsername(request.Username),
            Role = UserRole.User,
            IsMarketingDemo = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (telegramUserId is long newTg)
            await AttachTelegramUserIdAsync(user, newTg, ct);

        MarketingDemoConfigStore.ApplyToUser(user, request.Config);
        await _users.AddAsync(user, ct);

        await _audit.RecordAsync(
            action: "MarketingDemoCreated",
            actorUserId: _currentUser.UserId,
            targetUserId: user.Id,
            targetBinollaLinkId: null,
            previousState: null,
            newState: "True",
            detail: $"email={email};telegram_user_id={user.TelegramUserId}",
            ct: ct);

        _logger.LogInformation(
            "Admin created marketing demo user {UserId} email={Email} telegram={TelegramUserId}",
            user.Id, email, user.TelegramUserId);
        return MapDemo(user);
    }

    public async Task<MarketingDemoUserDto> SetMarketingDemoAsync(
        Guid userId,
        SetMarketingDemoRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

        if (user.Role == UserRole.Admin && request.IsMarketingDemo)
            throw new ApiException(ApiErrorCodes.ValidationError, "Admin accounts cannot be marketing demos.");

        var previous = user.IsMarketingDemo.ToString();
        user.IsMarketingDemo = request.IsMarketingDemo;

        var telegramUserId = NormalizeTelegramUserId(request.TelegramUserId);
        if (request.IsMarketingDemo && telegramUserId is long tgId)
            await AttachTelegramUserIdAsync(user, tgId, ct);

        if (request.Config is not null)
            MarketingDemoConfigStore.ApplyToUser(user, request.Config);
        else if (request.IsMarketingDemo && string.IsNullOrWhiteSpace(user.MarketingDemoConfigJson))
            MarketingDemoConfigStore.ApplyToUser(user, MarketingDemoConfigStore.Default);

        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);

        await _audit.RecordAsync(
            action: request.IsMarketingDemo ? "MarketingDemoEnabled" : "MarketingDemoDisabled",
            actorUserId: _currentUser.UserId,
            targetUserId: user.Id,
            targetBinollaLinkId: null,
            previousState: previous,
            newState: user.IsMarketingDemo.ToString(),
            detail: telegramUserId is null ? null : $"telegram_user_id={telegramUserId}",
            ct: ct);

        return MapDemo(user);
    }

    public async Task<MarketingDemoUserDto> UpdateMarketingDemoConfigAsync(
        Guid userId,
        UpdateMarketingDemoConfigRequest request,
        CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        if (request?.Config is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Config body is required.");

        var user = await _users.GetByIdAsync(userId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "User not found.", 404);

        if (!user.IsMarketingDemo)
            throw new ApiException(ApiErrorCodes.ValidationError, "User is not a marketing demo account.");

        var previous = user.MarketingDemoConfigJson;
        MarketingDemoConfigStore.ApplyToUser(user, request.Config);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _users.UpdateAsync(user, ct);

        await _audit.RecordAsync(
            action: "MarketingDemoConfigUpdated",
            actorUserId: _currentUser.UserId,
            targetUserId: user.Id,
            targetBinollaLinkId: null,
            previousState: previous is null ? null : "configured",
            newState: "configured",
            detail: $"balance={request.Config.Balance};profit={request.Config.TotalProfit};loss={request.Config.TotalLoss}",
            ct: ct);

        return MapDemo(user);
    }

    /// <summary>
    /// Bind a Telegram id to a marketing (or email) user. Absorbs empty Mini App stubs created by splash auth.
    /// </summary>
    private async Task AttachTelegramUserIdAsync(User target, long telegramUserId, CancellationToken ct)
    {
        if (target.TelegramUserId == telegramUserId)
            return;

        if (target.TelegramUserId is long existing && existing != telegramUserId)
        {
            throw new ApiException(
                ApiErrorCodes.TelegramTaken,
                "This account is already linked to a different Telegram user.",
                409);
        }

        var other = await _users.GetByTelegramUserIdAsync(telegramUserId, ct);
        if (other is not null && other.Id != target.Id)
        {
            if (!await IsAbsorbableTelegramStubAsync(other, ct))
            {
                throw new ApiException(
                    ApiErrorCodes.TelegramTaken,
                    "That Telegram user is already linked to another account.",
                    409);
            }

            other.TelegramUserId = null;
            other.UpdatedAt = DateTimeOffset.UtcNow;
            await _users.UpdateAsync(other, ct);
            _logger.LogInformation(
                "Absorbed Telegram stub {StubUserId} into {TargetUserId} for telegram_user_id {TelegramUserId}",
                other.Id, target.Id, telegramUserId);
        }

        target.TelegramUserId = telegramUserId;
    }

    private async Task<bool> IsAbsorbableTelegramStubAsync(User user, CancellationToken ct)
    {
        if (user.Role == UserRole.Admin || user.IsMarketingDemo)
            return false;
        if (!string.IsNullOrEmpty(user.Email) || !string.IsNullOrEmpty(user.PasswordHash))
            return false;

        var link = await _links.GetByUserIdAsync(user.Id, ct);
        return link is null;
    }

    private static void ApplyDemoProfile(User user, CreateMarketingDemoUserRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FullName))
            user.FullName = request.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(request.Username))
            user.Username = NormalizeUsername(request.Username);
    }

    private static long? NormalizeTelegramUserId(long? telegramUserId)
    {
        if (telegramUserId is null)
            return null;
        if (telegramUserId <= 0)
            throw new ApiException(ApiErrorCodes.ValidationError, "Telegram user id must be a positive number.");
        return telegramUserId;
    }

    /// <summary>
    /// Single writer for approval fields — keeps AdminApproved and ApprovalStatus consistent.
    /// </summary>
    private void ApplyApprovalState(BinollaLink link, AdminApprovalStatus status, bool approved)
    {
        var adminIdentity = $"{_currentUser.UserId}:{_currentUser.TelegramUserId?.ToString() ?? "web"}";
        link.AdminApproved = approved;
        link.ApprovalStatus = status;
        link.ApprovedAt = DateTimeOffset.UtcNow;
        link.ApprovedBy = adminIdentity;
        link.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task EnsureAdminAsync(CancellationToken ct)
    {
        if (!_currentUser.IsAdmin)
            throw new ApiException(ApiErrorCodes.Forbidden, "Admin role required.", 403);

        var user = await _users.GetByIdAsync(_currentUser.UserId, ct);
        if (user is null || user.Role != UserRole.Admin)
            throw new ApiException(ApiErrorCodes.Forbidden, "Admin role required.", 403);
    }

    private static AdminBinollaAccountDto Map(BinollaLink link, User user)
    {
        var hasEncryptedEmail = !string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail);
        var hasEncryptedPassword = !string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword);
        // #region agent log
        ScarAlpha.Binolla.Diagnostics.AgentDebug281dcf.Write(
            "A",
            "AdminAppService.Map",
            "admin_map_credentials",
            new
            {
                linkId = link.Id.ToString(),
                userId = user.Id.ToString(),
                hasEncryptedEmail,
                hasEncryptedPassword,
                appEmailPresent = !string.IsNullOrWhiteSpace(user.Email),
                dtoExposesBinollaLoginEmail = false,
                dtoExposesBinollaLoginPassword = false
            });
        // #endregion
        return new(
            Id: link.Id.ToString(),
            UserId: user.Id.ToString(),
            TelegramUserId: user.TelegramUserId,
            Email: user.Email,
            Username: user.Username,
            FullName: user.FullName,
            BinollaAccountIdentifier: link.BinollaAccountIdentifier,
            ConnectionStatus: link.Status.ToString(),
            ApprovalStatus: link.ApprovalStatus.ToString(),
            AdminApproved: link.AdminApproved,
            LastConnectedAt: link.LastConnectedAt,
            CreatedAt: link.CreatedAt,
            ApprovedAt: link.ApprovedAt,
            ApprovedBy: link.ApprovedBy);
    }

    private static MarketingDemoUserDto MapDemo(User user) =>
        new(
            Id: user.Id.ToString(),
            Email: user.Email,
            FullName: user.FullName,
            Username: user.Username,
            TelegramUserId: user.TelegramUserId,
            IsMarketingDemo: user.IsMarketingDemo,
            CreatedAt: user.CreatedAt,
            Config: MarketingDemoConfigStore.FromUser(user));

    private static string NormalizeEmail(string? email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@'))
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid email is required.");
        return normalized;
    }

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ApiException(ApiErrorCodes.ValidationError, "Password must be at least 8 characters.");
        if (password.Length > 128)
            throw new ApiException(ApiErrorCodes.ValidationError, "Password is too long.");
    }

    private static string? NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var value = username.Trim();
        if (value.StartsWith('@')) value = value[1..];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
