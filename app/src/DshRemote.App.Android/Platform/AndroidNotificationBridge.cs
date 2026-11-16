using Android.App;
using Android.Content;
using AndroidX.Core.App;
using DshRemote.App.Platform;

namespace DshRemote.App.Android.Platform;

/// <summary>Android implementation of the shared <see cref="INotificationBridge"/>.</summary>
internal sealed class AndroidNotificationBridge : INotificationBridge
{
    private const string Tag = "DshRemoteNotify";
    private readonly Context _context;

    public AndroidNotificationBridge(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    public void NotifyApprovalNeeded(string title, string body, string? sessionId)
        => Post(DshNotificationKind.Approval, title, body, sessionId);

    public void NotifyQuestionNeeded(string title, string body, string? sessionId)
        => Post(DshNotificationKind.Question, title, body, sessionId);

    public void NotifyTurnFinished(string title, string body)
        => Post(DshNotificationKind.TurnFinished, title, body, sessionId: null);

    private void Post(DshNotificationKind kind, string title, string body, string? sessionId)
    {
        try
        {
            AndroidNotifications.EnsureChannels(_context);

            var notificationId = AndroidNotifications.NotificationIdFor(kind, sessionId);
            var builder = new NotificationCompat.Builder(_context, AndroidNotifications.EventsChannelId);
            builder.SetSmallIcon(Resource.Drawable.ic_stat_dsh);
            builder.SetContentTitle(string.IsNullOrWhiteSpace(title) ? DefaultTitle(kind) : title);
            builder.SetContentText(body);
            var style = new NotificationCompat.BigTextStyle();
            style.BigText(body);
            builder.SetStyle(style);
            builder.SetAutoCancel(true);
            builder.SetOnlyAlertOnce(false);
            builder.SetPriority(NotificationCompat.PriorityHigh);
            builder.SetDefaults(NotificationCompat.DefaultAll);
            builder.SetVisibility(NotificationCompat.VisibilityPublic);
            builder.SetContentIntent(AndroidNotifications.CreateOpenAppIntent(_context, notificationId, sessionId));
            builder.SetColor(unchecked((int)0xFF7C5CFF));

            var notification = builder.Build()
                               ?? throw new InvalidOperationException("NotificationCompat.Builder.Build returned null");
            var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
            manager?.Notify(notificationId, notification);

            global::Android.Util.Log.Info(
                Tag,
                $"posted kind={kind} id={notificationId} session={(sessionId is { Length: > 0 } ? sessionId : "-")}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "post failed: " + ex);
        }
    }

    private static string DefaultTitle(DshNotificationKind kind) => kind switch
    {
        DshNotificationKind.Approval => "需要审批",
        DshNotificationKind.Question => "需要回答",
        _ => "任务完成"
    };
}
