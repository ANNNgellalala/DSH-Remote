using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using DshRemote.App.Platform;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Wires the Android implementations into the shared <see cref="PlatformServices"/> injection point.
/// Called from <c>DshRemoteAndroidApplication.OnCreate</c> (before Avalonia starts) and again from
/// <c>MainActivity.OnCreate</c> when a head is used without the custom Application class; idempotent.
/// </summary>
public static class AndroidPlatformHost
{
    private const string Tag = "DshRemotePlatform";
    private const int NotificationPermissionRequestCode = 0x4453;

    private static readonly object Sync = new();
    private static bool _initialized;

    /// <summary>Application context captured at initialization (null until <see cref="Initialize"/>).</summary>
    public static Context? AppContext { get; private set; }

    /// <summary>True once the platform services are registered.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Registers SecretStore, notifications and the foreground-service hooks. Safe to call twice.</summary>
    public static void Initialize(Context context)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            var app = context.ApplicationContext ?? context;
            AppContext = app;

            try
            {
                AndroidNotifications.EnsureChannels(app);

                // Create the app-owned external files dir early: it is where the E2E scripts push pairing.json
                // and image fixtures (adb-created dirs would be shell-owned and unreadable by the app).
                try
                {
                    var external = app.GetExternalFilesDir(null);
                    if (external is not null)
                    {
                        global::Android.Util.Log.Info(Tag, "external files dir: " + external.AbsolutePath);
                    }
                }
                catch (Exception dirEx)
                {
                    global::Android.Util.Log.Warn(Tag, "external files dir unavailable: " + dirEx.Message);
                }

                PlatformServices.PlatformName = "android";
                PlatformServices.SecretStore = new KeystoreSecretStore(app);
                NotificationBridge.Implementation = new AndroidNotificationBridge(app);
                PlatformServices.StartForegroundConnection = () => ConnectionForegroundService.Start(app);
                PlatformServices.StopForegroundConnection = () => ConnectionForegroundService.Stop(app);
                PlatformServices.ImagePicker = AndroidImagePicker.PickAsync;
                PlatformServices.QrScanner = AndroidQrScanner.ScanAsync;
                PlatformServices.ImageSaver = AndroidLongShotOutput.SaveAsync;
                PlatformServices.ImageSharer = AndroidLongShotOutput.ShareAsync;

                _initialized = true;
                global::Android.Util.Log.Info(
                    Tag,
                    "platform services initialized (keystore + notifications + foreground service + image picker + qr scanner + long screenshot saver)");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error(Tag, "platform initialization failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Asks for POST_NOTIFICATIONS on Android 13+ (runtime permission; without it notification-bar alerts
    /// are silently dropped while the foreground-service notification still keeps the process alive).
    /// </summary>
    public static void RequestNotificationPermission(Activity? activity)
    {
        if (activity is null || Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return;
        }

        const string permission = "android.permission.POST_NOTIFICATIONS";
        try
        {
            if (activity.CheckSelfPermission(permission) == Permission.Granted)
            {
                global::Android.Util.Log.Info(Tag, "POST_NOTIFICATIONS already granted");
                return;
            }

            activity.RequestPermissions(new[] { permission }, NotificationPermissionRequestCode);
            global::Android.Util.Log.Info(Tag, "requested POST_NOTIFICATIONS");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "notification permission request failed: " + ex);
        }
    }
}
