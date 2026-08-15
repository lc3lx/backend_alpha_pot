using ScarAlpha.Application.Abstractions;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Infrastructure.Notifications;

public sealed class NotificationWriter : INotificationWriter
{
    private readonly INotificationRepository _notifications;

    public NotificationWriter(INotificationRepository notifications) => _notifications = notifications;

    public Task AddAsync(
        Guid userId,
        string variant,
        string title,
        string description,
        Guid? tradeId = null,
        string? actionPath = null,
        CancellationToken ct = default)
    {
        return _notifications.AddAsync(new UserNotification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Variant = variant,
            Title = title,
            Description = description,
            Read = false,
            TradeId = tradeId,
            ActionPath = actionPath,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }
}
