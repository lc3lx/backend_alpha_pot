namespace ScarAlpha.Infrastructure.Workers;

public sealed class BinollaSessionRestoreOptions
{
    public const string SectionName = "Binolla:SessionRestore";

    /// <summary>When false, startup and lazy restore are no-ops.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Max parallel reconnects during a restore wave (prevents storms).</summary>
    public int MaxDegreeOfParallelism { get; set; } = 3;

    /// <summary>Per-user reconnect attempts with exponential backoff.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Initial backoff delay in milliseconds.</summary>
    public int InitialDelayMs { get; set; } = 500;

    /// <summary>Cap for exponential backoff.</summary>
    public int MaxDelayMs { get; set; } = 30_000;

    /// <summary>Lazy restore (access check) uses at most this many attempts.</summary>
    public int LazyMaxAttempts { get; set; } = 2;
}
