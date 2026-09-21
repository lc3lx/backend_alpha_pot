namespace ScarAlpha.Domain.Entities;

public enum TimeSignalStatus
{
    Pending = 0,
    Executing = 1,
    Executed = 2,
    Failed = 3,
    Cancelled = 4,
    Expired = 5
}

public class TimeSignal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Asset { get; set; } = string.Empty;
    public string RawAsset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty; // "CALL" or "PUT"
    public DateTimeOffset ScheduledTime { get; set; }
    public int DurationSeconds { get; set; } = 60;
    public TimeSignalStatus Status { get; set; } = TimeSignalStatus.Pending;
    public DateTimeOffset? ExecutedAt { get; set; }
    public int TargetCount { get; set; }
    public int ExecutedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ExecutionLog { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
