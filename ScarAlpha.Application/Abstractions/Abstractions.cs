using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByTelegramUserIdAsync(long telegramUserId, CancellationToken ct = default);
    Task<User> AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
}

public interface IBinollaLinkRepository
{
    Task<BinollaLink?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<BinollaLink?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BinollaLink>> ListAsync(AdminApprovalStatus? approvalStatus = null, CancellationToken ct = default);
    Task UpsertAsync(BinollaLink link, CancellationToken ct = default);
}

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<Trade?> GetByIdempotencyKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<Trade>> ListByUserAsync(
        Guid userId,
        int take,
        int skip = 0,
        TradeStatus? status = null,
        string? asset = null,
        CancellationToken ct = default);
    Task<int> CountByUserAsync(
        Guid userId,
        TradeStatus? status = null,
        string? asset = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<Trade>> ListOpenTradesAsync(CancellationToken ct = default);
    Task AddAsync(Trade trade, CancellationToken ct = default);
    Task UpdateAsync(Trade trade, CancellationToken ct = default);
}

public interface ITelegramAuthService
{
    TelegramAuthResult ValidateInitData(string initData);
}

public sealed class TelegramAuthResult
{
    public required long TelegramUserId { get; init; }
    public string? Username { get; init; }
    public string? FullName { get; init; }
    public string? LanguageCode { get; init; }
}

public interface IJwtTokenService
{
    string CreateToken(User user);
}

public interface ISecretProtector
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}

public interface ICurrentUser
{
    Guid UserId { get; }
    long TelegramUserId { get; }
    bool IsAdmin { get; }
}

public interface ITradeOutcomeWorker
{
    void Enqueue(Guid tradeId, Guid userId, string binollaOrderId);
}

/// <summary>
/// Serializes trade placement for the same user + idempotency key (in-process + DB unique index).
/// </summary>
public interface IIdempotencyGate
{
    Task<IAsyncDisposable> AcquireAsync(Guid userId, string key, CancellationToken ct = default);
}
