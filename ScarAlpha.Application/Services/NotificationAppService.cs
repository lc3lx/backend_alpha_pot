using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

public sealed class NotificationAppService
{
    private readonly ICurrentUser _currentUser;
    private readonly INotificationRepository _notifications;
    private readonly IMarketingDemoService _demo;

    public NotificationAppService(
        ICurrentUser currentUser,
        INotificationRepository notifications,
        IMarketingDemoService demo)
    {
        _currentUser = currentUser;
        _notifications = notifications;
        _demo = demo;
    }

    public async Task<NotificationListResponse> ListAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.BuildNotifications(_currentUser.UserId);

        var items = await _notifications.ListByUserAsync(_currentUser.UserId, 100, ct);
        var unread = await _notifications.CountUnreadAsync(_currentUser.UserId, ct);
        return new NotificationListResponse(items.Select(Map).ToList(), unread);
    }

    public async Task<NotificationDto> GetAsync(Guid id, CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
        {
            return _demo.FindNotification(_currentUser.UserId, id)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Notification not found.", 404);
        }

        var item = await _notifications.GetByIdAsync(id, _currentUser.UserId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Notification not found.", 404);
        return Map(item);
    }

    public async Task<NotificationDto> MarkReadAsync(Guid id, CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.MarkNotificationRead(_currentUser.UserId, id);

        var item = await _notifications.GetByIdAsync(id, _currentUser.UserId, ct)
                   ?? throw new ApiException(ApiErrorCodes.NotFound, "Notification not found.", 404);
        if (!item.Read)
        {
            item.Read = true;
            await _notifications.UpdateAsync(item, ct);
        }

        return Map(item);
    }

    public async Task<NotificationListResponse> MarkAllReadAsync(CancellationToken ct)
    {
        if (await _demo.IsMarketingDemoAsync(_currentUser.UserId, ct))
            return _demo.MarkAllNotificationsRead(_currentUser.UserId);

        await _notifications.MarkAllReadAsync(_currentUser.UserId, ct);
        return await ListAsync(ct);
    }

    private static NotificationDto Map(Domain.Entities.UserNotification n) =>
        new(
            Id: n.Id.ToString(),
            Variant: n.Variant,
            Title: n.Title,
            Description: n.Description,
            Read: n.Read,
            TradeId: n.TradeId?.ToString(),
            ActionPath: n.ActionPath,
            CreatedAt: n.CreatedAt);
}
