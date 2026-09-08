using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return _db.Users.FirstOrDefaultAsync(x => x.Email == normalized, ct);
    }

    public async Task<IReadOnlyList<User>> ListMarketingDemoUsersAsync(
        bool? activeOnly = true,
        CancellationToken ct = default)
    {
        var query = _db.Users.AsQueryable();
        if (activeOnly == true)
            query = query.Where(x => x.IsMarketingDemo);
        else if (activeOnly == false)
            query = query.Where(x => !x.IsMarketingDemo && x.MarketingDemoConfigJson != null);
        else
            query = query.Where(x => x.IsMarketingDemo || x.MarketingDemoConfigJson != null);

        return await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<User> Items, int Total)> SearchAsync(
        string? q,
        UserRole? role,
        bool? isMarketingDemo,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Users.AsQueryable();

        if (role is UserRole r)
            query = query.Where(x => x.Role == r);
        if (isMarketingDemo is bool demo)
            query = query.Where(x => x.IsMarketingDemo == demo);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            long? tg = long.TryParse(term, out var parsed) ? parsed : null;
            query = query.Where(x =>
                (x.Email != null && x.Email.Contains(term)) ||
                (x.FullName != null && x.FullName.ToLower().Contains(term)) ||
                (x.Username != null && x.Username.ToLower().Contains(term)) ||
                (tg != null && x.TelegramUserId == tg) ||
                x.Id.ToString().ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

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

    public async Task<IReadOnlyList<User>> ListWithBotRuntimeAsync(CancellationToken ct = default) =>
        await _db.Users
            .Where(x => x.BotRuntimeJson != null && x.BotRuntimeJson != "")
            .ToListAsync(ct);
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

    public async Task<(IReadOnlyList<BinollaLink> Items, int Total)> SearchAsync(
        AdminApprovalStatus? approvalStatus,
        string? q,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.BinollaLinks.AsQueryable();
        if (approvalStatus is not null)
            query = query.Where(x => x.ApprovalStatus == approvalStatus);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            var matchingUserIds = await _db.Users
                .Where(u =>
                    (u.Email != null && u.Email.Contains(term)) ||
                    (u.FullName != null && u.FullName.ToLower().Contains(term)) ||
                    (u.Username != null && u.Username.ToLower().Contains(term)) ||
                    u.Id.ToString().ToLower().Contains(term) ||
                    (u.TelegramUserId != null && u.TelegramUserId.ToString().Contains(term)))
                .Select(u => u.Id)
                .ToListAsync(ct);

            query = query.Where(x =>
                matchingUserIds.Contains(x.UserId) ||
                (x.BinollaAccountIdentifier != null && x.BinollaAccountIdentifier.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<BinollaLink>> ListWithStoredBinollaEmailAsync(CancellationToken ct = default)
    {
        var items = await _db.BinollaLinks
            .Where(x => x.EncryptedBinollaEmail != null && x.EncryptedBinollaEmail != "")
            .ToListAsync(ct);
        return items;
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
            if (!string.IsNullOrWhiteSpace(link.EncryptedBinollaEmail))
                existing.EncryptedBinollaEmail = link.EncryptedBinollaEmail;
            if (!string.IsNullOrWhiteSpace(link.EncryptedBinollaPassword))
                existing.EncryptedBinollaPassword = link.EncryptedBinollaPassword;
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
        BinollaAccountType? accountType = null,
        CancellationToken ct = default)
    {
        var q = Filter(_db.Trades.AsQueryable(), userId, status, asset, accountType);
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
        BinollaAccountType? accountType = null,
        CancellationToken ct = default) =>
        Filter(_db.Trades.AsQueryable(), userId, status, asset, accountType).CountAsync(ct);

    public async Task<IReadOnlyList<Trade>> ListOpenTradesAsync(CancellationToken ct = default) =>
        await _db.Trades
            .Where(x =>
                x.Status == TradeStatus.Pending ||
                x.Status == TradeStatus.Running ||
                x.Status == TradeStatus.Unknown ||
                x.Status == TradeStatus.Failed)
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<Trade> Items, int Total)> SearchAdminAsync(
        Guid? userId,
        TradeStatus? status,
        string? asset,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = _db.Trades.AsQueryable();
        if (userId is Guid uid)
            q = q.Where(x => x.UserId == uid);
        if (status is TradeStatus st)
            q = q.Where(x => x.Status == st);
        if (!string.IsNullOrWhiteSpace(asset))
            q = q.Where(x => x.Asset == asset);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

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
        string? asset,
        BinollaAccountType? accountType = null)
    {
        q = q.Where(x => x.UserId == userId);
        if (status.HasValue)
            q = q.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(asset))
            q = q.Where(x => x.Asset == asset);
        if (accountType.HasValue)
            q = q.Where(x => x.AccountType == accountType.Value);
        return q;
    }
}

public sealed class NotificationRepository : INotificationRepository
{
    private readonly AppDbContext _db;
    public NotificationRepository(AppDbContext db) => _db = db;

    public async Task AddAsync(UserNotification notification, CancellationToken ct = default)
    {
        _db.UserNotifications.Add(notification);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserNotification>> ListByUserAsync(
        Guid userId,
        int take,
        CancellationToken ct = default) =>
        await _db.UserNotifications
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

    public Task<UserNotification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default) =>
        _db.UserNotifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken ct = default) =>
        _db.UserNotifications.CountAsync(x => x.UserId == userId && !x.Read, ct);

    public async Task UpdateAsync(UserNotification notification, CancellationToken ct = default)
    {
        _db.UserNotifications.Update(notification);
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var unread = await _db.UserNotifications
            .Where(x => x.UserId == userId && !x.Read)
            .ToListAsync(ct);
        foreach (var item in unread)
            item.Read = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<UserNotification> Items, int Total)> SearchAdminAsync(
        Guid? userId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.UserNotifications.AsQueryable();
        if (userId is Guid uid)
            query = query.Where(x => x.UserId == uid);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }
}

public sealed class AppSettingRepository : IAppSettingRepository
{
    private readonly AppDbContext _db;

    public AppSettingRepository(AppDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, ct);
        return row?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null)
        {
            _db.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }
}

public sealed class ReferralRepository : IReferralRepository
{
    private readonly AppDbContext _db;
    public ReferralRepository(AppDbContext db) => _db = db;

    public Task<User?> GetUserByReferralCodeAsync(string code, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.ReferralCode == code, ct);

    public Task<bool> ReferralCodeExistsAsync(string code, CancellationToken ct = default) =>
        _db.Users.AnyAsync(x => x.ReferralCode == code, ct);

    public Task<ReferralQualification?> GetQualificationByReferredUserIdAsync(Guid referredUserId, CancellationToken ct = default) =>
        _db.ReferralQualifications.FirstOrDefaultAsync(x => x.ReferredUserId == referredUserId, ct);

    public async Task AddQualificationAsync(ReferralQualification qualification, CancellationToken ct = default)
    {
        _db.ReferralQualifications.Add(qualification);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateQualificationAsync(ReferralQualification qualification, CancellationToken ct = default)
    {
        _db.ReferralQualifications.Update(qualification);
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountQualifiedAsync(Guid referrerUserId, CancellationToken ct = default) =>
        _db.ReferralQualifications.CountAsync(x => x.ReferrerUserId == referrerUserId && x.Qualified, ct);

    public Task<int> CountAllAsync(Guid referrerUserId, CancellationToken ct = default) =>
        _db.ReferralQualifications.CountAsync(x => x.ReferrerUserId == referrerUserId, ct);

    public Task<int> CountActiveAsync(Guid referrerUserId, CancellationToken ct = default) =>
        // "Started trading" is FirstBotTradeAt being set — stamped on the referred user's
        // first settled real-account bot trade.
        //
        // Qualified counts too, and not only for tidiness: qualification requires active
        // trading days, so a qualified referral has necessarily traded. Rows that reached
        // that state before FirstBotTradeAt was being written would otherwise silently
        // drop out of their referrer's tier and cut the rate they are already earning.
        _db.ReferralQualifications.CountAsync(
            x => x.ReferrerUserId == referrerUserId && (x.FirstBotTradeAt != null || x.Qualified),
            ct);

    public async Task<(IReadOnlyList<ReferralQualification> Items, int Total)> ListByReferrerAsync(
        Guid referrerUserId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ReferralQualifications.Where(x => x.ReferrerUserId == referrerUserId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<ReferralQualification>> ListDepositWatchAsync(int take, CancellationToken ct = default) =>
        await _db.ReferralQualifications
            .Where(x => !x.Qualified)
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<Guid> ReferrerIds, int Total)> SearchReferrerIdsAsync(
        string? q, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ReferralQualifications.Select(x => x.ReferrerUserId).Distinct();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            var matchingUserIds = await _db.Users
                .Where(u =>
                    (u.Email != null && u.Email.Contains(term)) ||
                    (u.FullName != null && u.FullName.ToLower().Contains(term)) ||
                    (u.Username != null && u.Username.ToLower().Contains(term)) ||
                    (u.ReferralCode != null && u.ReferralCode.ToLower() == term) ||
                    u.Id.ToString().ToLower().Contains(term))
                .Select(u => u.Id)
                .ToListAsync(ct);
            query = query.Where(id => matchingUserIds.Contains(id));
        }

        var total = await query.CountAsync(ct);
        var ids = await query
            .OrderBy(x => x)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (ids, total);
    }

    public async Task<bool> AddCommissionAsync(ReferralCommission commission, CancellationToken ct = default)
    {
        // Checked up front (not just caught below) because the InMemory provider used in
        // tests does not enforce the unique index — only a real relational DB does.
        if (await _db.ReferralCommissions.AnyAsync(x => x.TradeId == commission.TradeId, ct))
            return false;

        _db.ReferralCommissions.Add(commission);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsUniqueViolation(ex, "referral_commissions"))
        {
            _db.Entry(commission).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, ReferralMemberTotals>> SummarizeByReferredAsync(
        Guid referrerUserId,
        CancellationToken ct = default)
    {
        var rows = await _db.ReferralCommissions
            .Where(x => x.ReferrerUserId == referrerUserId)
            .GroupBy(x => x.ReferredUserId)
            .Select(g => new
            {
                ReferredUserId = g.Key,
                Trades = g.Count(),
                Volume = g.Sum(x => x.TradeAmount),
                Commission = g.Sum(x => x.Amount)
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.ReferredUserId,
            r => new ReferralMemberTotals(r.Trades, r.Volume, r.Commission));
    }

    public Task<decimal> SumCommissionsAsync(Guid referrerUserId, CancellationToken ct = default) =>
        SumOrZeroAsync(_db.ReferralCommissions.Where(x => x.ReferrerUserId == referrerUserId).Select(x => x.Amount), ct);

    public async Task<(IReadOnlyList<ReferralCommission> Items, int Total)> ListCommissionsAsync(
        Guid referrerUserId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ReferralCommissions.Where(x => x.ReferrerUserId == referrerUserId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<bool> RewardExistsAsync(Guid userId, ReferralRewardKind kind, int tier, CancellationToken ct = default) =>
        _db.ReferralRewards.AnyAsync(x => x.UserId == userId && x.Kind == kind && x.Tier == tier, ct);

    public async Task<bool> AddRewardAsync(ReferralReward reward, CancellationToken ct = default)
    {
        if (await _db.ReferralRewards.AnyAsync(
                x => x.UserId == reward.UserId && x.Kind == reward.Kind && x.Tier == reward.Tier, ct))
            return false;

        _db.ReferralRewards.Add(reward);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsUniqueViolation(ex, "referral_rewards"))
        {
            _db.Entry(reward).State = EntityState.Detached;
            return false;
        }
    }

    public Task<decimal> SumRewardsAsync(Guid userId, IReadOnlyCollection<ReferralRewardStatus> statuses, CancellationToken ct = default) =>
        SumOrZeroAsync(
            _db.ReferralRewards.Where(x => x.UserId == userId && statuses.Contains(x.Status)).Select(x => x.Amount), ct);

    public async Task<IReadOnlyList<ReferralReward>> ListRewardsAsync(Guid userId, CancellationToken ct = default) =>
        await _db.ReferralRewards
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.GrantedAt)
            .ToListAsync(ct);

    public Task<ReferralReward?> GetRewardByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.ReferralRewards.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task UpdateRewardAsync(ReferralReward reward, CancellationToken ct = default)
    {
        _db.ReferralRewards.Update(reward);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddPayoutAsync(ReferralPayout payout, CancellationToken ct = default)
    {
        _db.ReferralPayouts.Add(payout);
        await _db.SaveChangesAsync(ct);
    }

    public Task<decimal> SumPayoutsAsync(Guid userId, IReadOnlyCollection<ReferralPayoutStatus> statuses, CancellationToken ct = default) =>
        SumOrZeroAsync(
            _db.ReferralPayouts.Where(x => x.UserId == userId && statuses.Contains(x.Status)).Select(x => x.Amount), ct);

    public Task<bool> HasPendingPayoutAsync(Guid userId, CancellationToken ct = default) =>
        _db.ReferralPayouts.AnyAsync(x => x.UserId == userId && x.Status == ReferralPayoutStatus.Pending, ct);

    public async Task<(IReadOnlyList<ReferralPayout> Items, int Total)> ListPayoutsByUserAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ReferralPayouts.Where(x => x.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.RequestedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<(IReadOnlyList<ReferralPayout> Items, int Total)> SearchPayoutsAsync(
        ReferralPayoutStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ReferralPayouts.AsQueryable();
        if (status is ReferralPayoutStatus s)
            query = query.Where(x => x.Status == s);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(x => x.Status == ReferralPayoutStatus.Pending ? 0 : 1)
            .ThenByDescending(x => x.RequestedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<ReferralPayout?> GetPayoutByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.ReferralPayouts.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task UpdatePayoutAsync(ReferralPayout payout, CancellationToken ct = default)
    {
        _db.ReferralPayouts.Update(payout);
        await _db.SaveChangesAsync(ct);
    }

    private static async Task<decimal> SumOrZeroAsync(IQueryable<decimal> query, CancellationToken ct) =>
        await query.AnyAsync(ct) ? await query.SumAsync(ct) : 0m;

    private static bool IsUniqueViolation(Exception ex, string table)
    {
        var text = ex.ToString();
        return text.Contains(table, StringComparison.OrdinalIgnoreCase)
               || text.Contains("23505", StringComparison.Ordinal); // PostgreSQL unique_violation
    }
}

/// <summary>
/// Lets the singleton maintenance service reach the scoped DbContext.
///
/// The flag is read on every worker tick, so the service itself must be a singleton;
/// EF's context is scoped. This opens a short-lived scope per access, which is fine
/// because reads come from the service's in-memory copy and writes are rare.
/// </summary>
public sealed class ScopedAppSettingRepository : IAppSettingRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedAppSettingRepository(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredService<IAppSettingRepository>();
        return await inner.GetAsync(key, ct);
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredService<IAppSettingRepository>();
        await inner.SetAsync(key, value, ct);
    }
}
