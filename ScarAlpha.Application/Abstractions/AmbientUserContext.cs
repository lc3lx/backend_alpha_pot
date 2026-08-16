namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Lets background workers (bot loop, session restore) act as a specific user
/// without an HTTP JWT context.
/// </summary>
public static class AmbientUserContext
{
    private static readonly AsyncLocal<Guid?> Override = new();

    public static Guid? OverrideUserId => Override.Value;

    public static IDisposable Use(Guid userId)
    {
        var previous = Override.Value;
        Override.Value = userId;
        return new Reset(previous);
    }

    private sealed class Reset(Guid? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
