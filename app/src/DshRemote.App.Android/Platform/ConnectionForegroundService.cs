using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Keeps the app process alive while a connection is open. The service deliberately holds no connection
/// state or sockets: the Avalonia/Core client keeps working inside the same process, and Android only
/// needs a foreground service so it does not freeze/kill the process while the UI is in the background.
/// </summary>
/// <remarks>
/// Registered in the merged manifest through this attribute, which also declares
/// <c>android:foregroundServiceType="dataSync"</c> (required on Android 14+ together with the
/// <c>FOREGROUND_SERVICE_DATA_SYNC</c> permission).
/// </remarks>
[Service(
    Name = "dev.dshremote.app.ConnectionForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ConnectionForegroundService : Service
{
    private const string Tag = "DshRemoteFgs";

    // Android requires Service.startForeground() within ~5s of Context.startForegroundService(). If the
    // shared layer flips the connection off again inside that window (e.g. an instantly-failing connect),
    // calling stopService() first makes the system throw ForegroundServiceDidNotStartInTimeException and kill
    // the process. Track the window and defer such a stop until the service is actually foregrounded.
    private static readonly object StateSync = new();
    private static long _startRequestedAtTicks;
    private static bool _foregroundPosted;

    /// <summary>Starts (or re-posts) the foreground service. Safe to call repeatedly.</summary>
    public static void Start(Context context)
    {
        try
        {
            lock (StateSync)
            {
                _startRequestedAtTicks = System.Environment.TickCount64;
            }

            var intent = new Intent(context, typeof(ConnectionForegroundService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                StartForegroundServiceCompat(context, intent);
            }
            else
            {
                context.StartService(intent);
            }

            global::Android.Util.Log.Info(Tag, "start requested");
        }
        catch (Exception ex)
        {
            // Android 12+ can forbid starting an FGS from the background; never crash the connection logic.
            global::Android.Util.Log.Warn(Tag, "start failed: " + ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static void StartForegroundServiceCompat(Context context, Intent intent)
        => context.StartForegroundService(intent);

    /// <summary>Stops the foreground service (idempotent, safe inside the start window).</summary>
    public static void Stop(Context context)
    {
        try
        {
            bool defer;
            lock (StateSync)
            {
                var sinceStart = System.Environment.TickCount64 - _startRequestedAtTicks;
                defer = !_foregroundPosted && _startRequestedAtTicks != 0 && sinceStart < 5_000;
            }

            if (defer)
            {
                global::Android.Util.Log.Info(Tag, "stop deferred until startForeground() completed");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5_500).ConfigureAwait(false);
                    StopServiceNow(context);
                });
                return;
            }

            StopServiceNow(context);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "stop failed: " + ex);
        }
    }

    private static void StopServiceNow(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(ConnectionForegroundService)));
            global::Android.Util.Log.Info(Tag, "stop requested");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "stop failed: " + ex);
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var notification = BuildNotification();
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForegroundTyped(notification);
            }
            else
            {
                StartForeground(AndroidNotifications.StatusNotificationId, notification);
            }

            lock (StateSync)
            {
                _foregroundPosted = true;
            }

            global::Android.Util.Log.Info(Tag, "foreground notification posted");
            return StartCommandResult.Sticky;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "StartForeground failed: " + ex);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private void StartForegroundTyped(Notification notification)
        => StartForeground(
            AndroidNotifications.StatusNotificationId,
            notification,
            ForegroundService.TypeDataSync);

    public override void OnDestroy()
    {
        global::Android.Util.Log.Info(Tag, "service destroyed");
        lock (StateSync)
        {
            _foregroundPosted = false;
            _startRequestedAtTicks = 0;
        }

        try
        {
            // minSdk is 24, so the API-24 StopForeground(int) overload is always available.
            StopForeground(StopForegroundFlags.Remove);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "StopForeground failed: " + ex);
        }

        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var openApp = AndroidNotifications.CreateOpenAppIntent(this, AndroidNotifications.StatusNotificationId, null);
        var builder = new NotificationCompat.Builder(this, AndroidNotifications.StatusChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_dsh);
        builder.SetContentTitle("DSH Remote 已连接");
        builder.SetContentText("正在后台保持与桌面的连接");
        builder.SetContentIntent(openApp);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetShowWhen(false);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetVisibility(NotificationCompat.VisibilityPublic);
        builder.SetColor(unchecked((int)0xFF7C5CFF));

        return builder.Build()
               ?? throw new InvalidOperationException("NotificationCompat.Builder.Build returned null");
    }
}

