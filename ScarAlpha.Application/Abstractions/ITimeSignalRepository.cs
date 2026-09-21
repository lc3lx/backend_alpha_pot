using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Application.Abstractions;

public interface ITimeSignalRepository
{
    Task AddAsync(TimeSignal signal, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<TimeSignal> signals, CancellationToken ct = default);
    Task<TimeSignal?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<(IReadOnlyList<TimeSignal> Items, int Total)> ListAsync(
        TimeSignalStatus? status,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<IReadOnlyList<TimeSignal>> GetPendingDueAsync(DateTimeOffset now, int take = 10, CancellationToken ct = default);
    Task UpdateAsync(TimeSignal signal, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
