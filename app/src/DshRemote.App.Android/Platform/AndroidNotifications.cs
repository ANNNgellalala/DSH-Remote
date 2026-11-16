using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace DshRemote.App.Android.Platform;

/// <summary>Notification channels, ids and tap intents shared by the foreground service and event alerts.</summary>
internal static class AndroidNotifications
{
    /// <summary>Low-importance persistent channel for the connection foreground service.</summary>
    public const string StatusChannelId = "dsh_remote_status";

    /// <summary>High-importance channel for approvals / questions / turn-finished alerts.</summary>
    public const string EventsChannelId = "dsh_remote_events";

    /// <summary>Notification id owned by <c>ConnectionForegroundService</c>.</summary>
    public const int StatusNotificationId = 0x44530001;

    /// <summary>Creates both channels (idempotent; Android 8.0+ only).</summary>
    public static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        EnsureChannelsCore(context);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static void EnsureChannelsCore(Context context)
    {
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null)
        {
            return;
        }

        var status = new NotificationChannel(StatusChannelId, "连接保活", NotificationImportance.Low)
        {
            Description = "保持与桌面 DSH 的连接时显示的常驻通知。"
        };
        status.SetShowBadge(false);
        status.LockscreenVisibility = NotificationVisibility.Public;
        manager.CreateNotificationChannel(status);

        var events = new NotificationChannel(EventsChannelId, "提醒", NotificationImportance.High)
        {
            Description = "审批请求、提问与任务完成提醒。"
        };
        events.EnableVibration(true);
        events.SetShowBadge(true);
        events.LockscreenVisibility = NotificationVisibility.Public;
        manager.CreateNotificationChannel(events);
    }

    /// <summary>
    /// Stable notification id per (kind, sessionId). Hashing the session means repeated alerts for the same
    /// session replace each other instead of piling up.
    /// </summary>
    public static int NotificationIdFor(DshNotificationKind kind, string? sessionId)
    {
        var hash = StableHash(sessionId ?? string.Empty);
        return (int)kind * 100_000 + (int)(hash % 100_000u);
    }

    /// <summary>Tap intent: opens <see cref="MainActivity"/> (singleTop) carrying the optional session id.</summary>
    public static PendingIntent CreateOpenAppIntent(Context context, int requestCode, string? sessionId)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop | ActivityFlags.NewTask);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            intent.PutExtra(MainActivity.ExtraSessionId, sessionId);
        }

        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            flags |= PendingIntentFlags.Immutable;
        }

        return PendingIntent.GetActivity(context, requestCode, intent, flags)!;
    }

    /// <summary>FNV-1a: stable across processes (string.GetHashCode is randomized per process).</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}

/// <summary>Notification categories; the numeric values are part of the public notification id contract.</summary>
public enum DshNotificationKind
{
    Approval = 1,
    Question = 2,
    TurnFinished = 3
}
