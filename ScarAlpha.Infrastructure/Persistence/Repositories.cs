using Microsoft.EntityFrameworkCore;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Persistence;

public sealed class UserRepository : IUserRepository
{
    private readonly AppDbContext _db;
    public UserRepository(AppDbContext db) => _db = db;

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<User?> GetByTelegramUserIdAsync(long telegramUserId, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId, ct);

    public async Task<User> AddAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }
}

public sealed class BinollaLinkRepository : IBinollaLinkRepository
{
    private readonly AppDbContext _db;
    public BinollaLinkRepository(AppDbContext db) => _db = db;

    public Task<BinollaLink?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        _db.BinollaLinks.FirstOrDefaultAsync(x => x.UserId == userId, ct);

    public Task<BinollaLink?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.BinollaLinks.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<BinollaLink>> ListAsync(
        AdminApprovalStatus? approvalStatus = null,
        CancellationToken ct = default)
    {
        var query = _db.BinollaLinks.AsQueryable();
        if (approvalStatus is not null)
            query = query.Where(x => x.ApprovalStatus == approvalStatus);
        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task UpsertAsync(BinollaLink link, CancellationToken ct = default)
    {
        var existing = await _db.BinollaLinks.FirstOrDefaultAsync(x => x.UserId == link.UserId, ct);
        if (existing is null)
        {
            _db.BinollaLinks.Add(link);
        }
        else
        {
            existing.EncryptedSsid = link.EncryptedSsid;
            existing.EncryptedCookieHeader = link.EncryptedCookieHeader;
            existing.AccountType = link.AccountType;
            existing.Status = link.Status;
            existing.BinollaAccountIdentifier = link.BinollaAccountIdentifier;
            existing.ReferralStatus = link.ReferralStatus;
            existing.ReferralCheckedAt = link.ReferralCheckedAt;
            existing.AdminApproved = link.AdminApproved;
            existing.ApprovalStatus = link.ApprovalStatus;
            existing.ApprovedAt = link.ApprovedAt;
            existing.ApprovedBy = link.ApprovedBy;
            existing.LastConnectedAt = link.LastConnectedAt;
            existing.UpdatedAt = link.UpdatedAt;
        }

        await _db.SaveChangesAsync(ct);
    }
}

public sealed class TradeRepository : ITradeRepository
{
    private readonly AppDbContext _db;
    public TradeRepository(AppDbContext db) => _db = db;

    public Task<Trade?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default) =>
        _db.Trades.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public Task<Trade?> GetByIdempotencyKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct = default) =>
        _db.Trades.FirstOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == idempotencyKey, ct);

    public async Task<IReadOnlyList<Trade>> ListByUserAsync(
        Guid userId,
        int take,
        int skip = 0,
        TradeStatus? status = null,
        string? asset = null,
        CancellationToken ct = default)
    {
        var q = Filter(_db.Trades.AsQueryable(), userId, status, asset);
        return await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountByUserAsync(
        Guid userId,
        TradeStatus? status = null,
        string? asset = null,
        CancellationToken ct = default) =>
        Filter(_db.Trades.AsQueryable(), userId, status, asset).CountAsync(ct);

    public async Task<IReadOnlyList<Trade>> ListOpenTradesAsync(CancellationToken ct = default) =>
        await _db.Trades
            .Where(x => x.Status == TradeStatus.Pending || x.Status == TradeStatus.Running)
            .ToListAsync(ct);

    public async Task AddAsync(Trade trade, CancellationToken ct = default)
    {
        _db.Trades.Add(trade);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Trade trade, CancellationToken ct = default)
    {
        _db.Trades.Update(trade);
        await _db.SaveChangesAsync(ct);
    }

    private static IQueryable<Trade> Filter(
        IQueryable<Trade> q,
        Guid userId,
        TradeStatus? status,
        string? asset)
    {
        q = q.Where(x => x.UserId == userId);
        if (status.HasValue)
            q = q.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(asset))
            q = q.Where(x => x.Asset == asset);
        return q;
    }
}
