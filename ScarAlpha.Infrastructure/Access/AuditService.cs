using Microsoft.EntityFrameworkCore;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Infrastructure.Persistence;

namespace ScarAlpha.Infrastructure.Access;

public sealed class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    public AuditService(AppDbContext db) => _db = db;

    public async Task RecordAsync(
        string action,
        Guid actorUserId,
        Guid? targetUserId,
        Guid? targetBinollaLinkId,
        string? previousState,
        string? newState,
        string? detail = null,
        CancellationToken ct = default)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Action = action,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            TargetBinollaLinkId = targetBinollaLinkId,
            PreviousState = previousState,
            NewState = newState,
            Detail = detail,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListForTargetUserAsync(
        Guid targetUserId,
        int take,
        CancellationToken ct = default) =>
        await _db.AuditEvents
            .Where(x => x.TargetUserId == targetUserId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<AuditEvent> Items, int Total)> SearchAsync(
        Guid? targetUserId,
        string? action,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.AuditEvents.AsQueryable();
        if (targetUserId is Guid uid)
            query = query.Where(x => x.TargetUserId == uid);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var a = action.Trim();
            query = query.Where(x => x.Action == a);
        }
        if (from is DateTimeOffset f)
            query = query.Where(x => x.CreatedAt >= f);
        if (to is DateTimeOffset t)
            query = query.Where(x => x.CreatedAt <= t);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }
}
