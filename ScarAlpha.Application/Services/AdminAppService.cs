using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Services;

public sealed class AdminAppService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ApprovalGates = new();

    private readonly ICurrentUser _currentUser;
    private readonly IBinollaLinkRepository _links;
    private readonly IUserRepository _users;
    private readonly IAuditService _audit;
    private readonly ILogger<AdminAppService> _logger;

    public AdminAppService(
        ICurrentUser currentUser,
        IBinollaLinkRepository links,
        IUserRepository users,
        IAuditService audit,
        ILogger<AdminAppService> logger)
    {
        _currentUser = currentUser;
        _links = links;
        _users = users;
        _audit = audit;
        _logger = logger;
    }

    public async Task<AdminBinollaAccountListResponse> ListAsync(string? status, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        AdminApprovalStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AdminApprovalStatus>(status, ignoreCase: true, out var parsed))
                throw new ApiException(ApiErrorCodes.ValidationError, "status must be Pending, Approved, or Rejected.");
            filter = parsed;
        }

        var links = await _links.ListAsync(filter, ct);
        var items = new List<AdminBinollaAccountDto>();
        foreach (var link in links)
        {
            var user = await _users.GetByIdAsync(link.UserId, ct);
            if (user is null) continue;
            items.Add(Map(link, user));
        }

        return new AdminBinollaAccountListResponse(items, items.Count);
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

            return Map(link, user);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Single writer for approval fields — keeps AdminApproved and ApprovalStatus consistent.
    /// </summary>
    private void ApplyApprovalState(Domain.Entities.BinollaLink link, AdminApprovalStatus status, bool approved)
    {
        var adminIdentity = $"{_currentUser.UserId}:{_currentUser.TelegramUserId}";
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

    private static AdminBinollaAccountDto Map(Domain.Entities.BinollaLink link, Domain.Entities.User user) =>
        new(
            Id: link.Id.ToString(),
            UserId: user.Id.ToString(),
            TelegramUserId: user.TelegramUserId,
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
