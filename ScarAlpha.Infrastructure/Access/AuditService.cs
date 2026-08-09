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
}
