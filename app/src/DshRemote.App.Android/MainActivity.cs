using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.View;
using Avalonia.Android;
using Avalonia.Threading;
using DshRemote.App.Android.Platform;
using DshRemote.App.Platform;
using DshRemote.App.Services;
using DshRemote.Core;

namespace DshRemote.App.Android;

/// <summary>
/// Single Avalonia activity. Besides hosting the shared UI it is the platform entry point for M3:
/// it dispatches <c>dshremote://pair?...</c> deep links into the shared layer, asks for the Android 13+
/// notification permission, and exposes small adb diagnostics hooks used by the E2E scripts.
/// </summary>
[Activity(
    Label = "DSH Remote",
    Theme = "@style/DshRemoteTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden
        | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = DeepLinkScheme)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>Scheme produced by the relay pairing command (<c>dshremote://pair?...</c>).</summary>
    public const string DeepLinkScheme = "dshremote";

    /// <summary>Optional session id carried by notification taps.</summary>
    public const string ExtraSessionId = "dev.dshremote.app.extra.SESSION_ID";

    // Diagnostics hooks (documented in scripts/e2e): adb can start the foreground service or post a test
    // notification without clicking through the UI.
    public const string ExtraStartForegroundService = "dev.dshremote.app.extra.START_FOREGROUND_SERVICE";
    public const string ExtraStopForegroundService = "dev.dshremote.app.extra.STOP_FOREGROUND_SERVICE";
    public const string ExtraNotifyTest = "dev.dshremote.app.extra.NOTIFY_TEST";
    public const string ExtraKeystoreTest = "dev.dshremote.app.extra.KEYSTORE_TEST";

    /// <summary>M5 diagnostics: absolute path of an image the picker should return without UI (emulator E2E).</summary>
    public const string ExtraImagePath = "dshremote.imagePath";

    /// <summary>M5 diagnostics: open the real system photo picker and log the result (manual/E2E check).</summary>
    public const string ExtraImagePickUi = "dshremote.imagePickUi";

    private const string Tag = "DshRemoteActivity";

    private View? _decorView;
    private ViewTreeObserver.IOnGlobalLayoutListener? _insetsListener;
    private string? _lastInsetsSignature;
    private AndroidX.Activity.OnBackPressedCallback? _backPressedCallback;

    private readonly object _imagePickSync = new();
    private ActivityResultLauncher? _photoPickerLauncher;
    private ActivityResultLauncher? _getContentLauncher;
    private TaskCompletionSource<DshImageAttachment?>? _pendingImagePick;

    // M6: QR scanner (system camera/gallery chooser + local ZXing decode).
    private const int CameraPermissionRequestCode = 0x4454;
    private readonly object _qrScanSync = new();
    private ActivityResultLauncher? _qrScanLauncher;
    private TaskCompletionSource<string?>? _pendingQrScan;
    private global::Android.Net.Uri? _qrCaptureUri;
    private TaskCompletionSource<bool>? _cameraPermissionCompletion;
    private string? _qrScannerDebugImage;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        AndroidPlatformHost.Initialize(this);
        AndroidPlatformHost.RequestNotificationPermission(this);
        InstallInsetsTracking();
        InstallBackHandling();
        InstallImagePicker();
        InstallQrScanner();
        HandleIntent(Intent, "activity-create");
    }

    /// <summary>
    /// Registers the shared-layer back handler with the AppCompat back dispatcher. This is what receives the
    /// back gesture on Android 13+/15+ (predictive back is enabled by default for targetSdk 35+ and bypasses
    /// <c>OnBackPressed</c>, which stays as the pre-13 fallback below).
    /// </summary>
    private void InstallBackHandling()
    {
        try
        {
            _backPressedCallback = new BackPressedCallback(this);
            OnBackPressedDispatcher?.AddCallback(this, _backPressedCallback);
            global::Android.Util.Log.Info(Tag, "[BackInvoked] dispatcher callback registered");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[BackInvoked] registration failed: " + ex.Message);
        }
    }

    private void UninstallBackHandling()
    {
        try
        {
            _backPressedCallback?.Remove();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[BackInvoked] unregister failed: " + ex.Message);
        }
        finally
        {
            _backPressedCallback = null;
        }
    }

    private void HandleBackInvoked()
    {
        var handled = false;
        try
        {
            handled = PlatformServices.HandleBack();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "BackHandler failed: " + ex.Message);
        }

        global::Android.Util.Log.Info(Tag, $"[BackInvoked] consumed={handled}");
        if (!handled)
        {
            Finish();
        }
    }

    // ---- M5: image picker ------------------------------------------------------------------------

    private void InstallImagePicker()
    {
        try
        {
            _photoPickerLauncher = RegisterForActivityResult(
                new ActivityResultContracts.PickVisualMedia(),
                new ImagePickCallback(this));
            _getContentLauncher = RegisterForActivityResult(
                new ActivityResultContracts.GetContent(),
                new ImagePickCallback(this));
            AndroidImagePicker.Attach(this);
            global::Android.Util.Log.Info(
                Tag,
                "[ImagePicker] launchers registered (PickVisualMedia primary + GetContent fallback)");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[ImagePicker] launcher registration failed: " + ex);
        }
    }

    /// <summary>Called by <see cref="AndroidImagePicker"/> when the shared layer asks for a picture.</summary>
    internal Task<DshImageAttachment?> PickImageFromGalleryAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<DshImageAttachment?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_imagePickSync)
        {
            if (_pendingImagePick is not null)
            {
                global::Android.Util.Log.Warn(Tag, "[ImagePicker] pick already in progress; reusing it");
                return _pendingImagePick.Task;
            }

            _pendingImagePick = completion;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_imagePickSync)
                {
                    if (ReferenceEquals(_pendingImagePick, completion))
                    {
                        _pendingImagePick = null;
                    }
                }

                completion.TrySetCanceled(cancellationToken);
                global::Android.Util.Log.Info(Tag, "[ImagePicker] pick canceled by caller");
            });
        }

        Dispatcher.UIThread.Post(() => LaunchImagePicker(completion));
        return completion.Task;
    }

    private void LaunchImagePicker(TaskCompletionSource<DshImageAttachment?> completion)
    {
        try
        {
            // Prefer the Android photo picker (no storage permission needed); fall back to a content pick.
            if (_photoPickerLauncher is not null)
            {
                var request = new PickVisualMediaRequest.Builder()
                    .SetMediaType(ActivityResultContracts.PickVisualMedia.ImageOnly.Instance)
                    .Build();
                _photoPickerLauncher.Launch(request);
                global::Android.Util.Log.Info(Tag, "[ImagePicker] launched PickVisualMedia");
                return;
            }

            if (_getContentLauncher is not null)
            {
                _getContentLauncher.Launch("image/*");
                global::Android.Util.Log.Info(Tag, "[ImagePicker] launched GetContent fallback");
                return;
            }

            throw new InvalidOperationException("no image picker launcher registered");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(
                Tag,
                "[ImagePicker] PickVisualMedia failed, trying GetContent: " + ex.Message);
            try
            {
                if (_getContentLauncher is not null)
                {
                    _getContentLauncher.Launch("image/*");
                    global::Android.Util.Log.Info(Tag, "[ImagePicker] launched GetContent fallback");
                    return;
                }
            }
            catch (Exception fallbackEx)
            {
                global::Android.Util.Log.Error(Tag, "[ImagePicker] GetContent fallback failed: " + fallbackEx);
            }

            lock (_imagePickSync)
            {
                if (ReferenceEquals(_pendingImagePick, completion))
                {
                    _pendingImagePick = null;
                }
            }

            completion.TrySetResult(null);
        }
    }

    private void OnImagePicked(global::Android.Net.Uri? uri)
    {
        TaskCompletionSource<DshImageAttachment?>? completion;
        lock (_imagePickSync)
        {
            completion = _pendingImagePick;
            _pendingImagePick = null;
        }

        if (completion is null)
        {
            global::Android.Util.Log.Warn(Tag, "[ImagePicker] result with no pending pick (canceled earlier?)");
            return;
        }

        if (uri is null)
        {
            global::Android.Util.Log.Info(Tag, "[ImagePicker] user canceled the picker");
            completion.TrySetResult(null);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var image = await AndroidImagePicker
                    .ReadFromUriAsync(this, uri, CancellationToken.None)
                    .ConfigureAwait(false);
                completion.TrySetResult(image);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error(Tag, "[ImagePicker] read failed: " + ex);
                completion.TrySetResult(null);
            }
        });
    }

    private sealed class ImagePickCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _activity;

        public ImagePickCallback(MainActivity activity) => _activity = activity;

        public void OnActivityResult(Java.Lang.Object? result)
            => _activity.OnImagePicked(result as global::Android.Net.Uri);
    }

    /// <summary>
    /// adb diagnostics for M5: runs <see cref="PlatformServices.PickImageAsync"/> end-to-end and logs the
    /// resulting bytes/mime/dimensions so the emulator E2E script can assert the picker pipeline.
    /// </summary>
    private async void RunImagePickerSelfTest(bool useDebugPath)
    {
        try
        {
            global::Android.Util.Log.Info(Tag, $"[ImagePicker] self-test start debugPath={useDebugPath}");
            var image = await PlatformServices.PickImageAsync().ConfigureAwait(false);
            if (image is null)
            {
                global::Android.Util.Log.Info(Tag, "[ImagePicker] self-test result=none");
                return;
            }

            var dimensions = ImageCompressor.ReadBounds(image.Data);
            global::Android.Util.Log.Info(
                Tag,
                $"[ImagePicker] self-test ok bytes={image.Data.Length} mime={image.MediaType} " +
                $"name={image.Name ?? "-"} dims={dimensions.Width}x{dimensions.Height}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[ImagePicker] self-test failed: " + ex);
        }
    }

    /// <summary>The real photo-picker UI can only be launched once the activity is RESUMED.</summary>
    private void ScheduleImagePickerUiSelfTest()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2_000).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                AndroidImagePicker.DebugImagePath = null;
                RunImagePickerSelfTest(useDebugPath: false);
            });
        });
    }

    // ---- M6: QR scanner (system camera/gallery + ZXing) -------------------------------------------

    private void InstallQrScanner()
    {
        try
        {
            _qrScanLauncher = RegisterForActivityResult(
                new ActivityResultContracts.StartActivityForResult(),
                new QrScanCallback(this));
            AndroidQrScanner.Attach(this);
            global::Android.Util.Log.Info(
                Tag,
                "[QrScan] launcher registered (system camera/gallery chooser + ZXing decode)");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[QrScan] launcher registration failed: " + ex);
        }
    }

    /// <summary>Called by <see cref="AndroidQrScanner"/> when the shared layer asks to scan a QR code.</summary>
    internal Task<string?> ScanQrFromSystemAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_qrScanSync)
        {
            if (_pendingQrScan is not null)
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] scan already in progress; reusing it");
                return _pendingQrScan.Task;
            }

            _pendingQrScan = completion;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_qrScanSync)
                {
                    if (ReferenceEquals(_pendingQrScan, completion))
                    {
                        _pendingQrScan = null;
                    }
                }

                completion.TrySetCanceled(cancellationToken);
                global::Android.Util.Log.Info(Tag, "[QrScan] scan canceled by caller");
            });
        }

        Dispatcher.UIThread.Post(() => LaunchQrScanner(completion));
        return completion.Task;
    }

    private void LaunchQrScanner(TaskCompletionSource<string?> completion)
    {
        // Primary path: our own Camera2 scanner Activity. OEM camera apps are unreliable inside the
        // ACTION_IMAGE_CAPTURE chooser (ColorOS returns without writing the output file), so the built-in
        // scanner is preferred; the system chooser stays as a fallback for devices where Camera2 is unusable.
        try
        {
            var scanner = new Intent(this, typeof(QrScannerActivity));
            if (!string.IsNullOrWhiteSpace(_qrScannerDebugImage))
            {
                scanner.PutExtra(QrScannerActivity.ExtraDebugImage, _qrScannerDebugImage);
            }

            _qrScanLauncher!.Launch(scanner);
            global::Android.Util.Log.Info(
                Tag,
                "[QrScan] launched in-app camera scanner" +
                (string.IsNullOrWhiteSpace(_qrScannerDebugImage) ? string.Empty : " (debug image)"));
            return;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] in-app scanner unavailable, using system chooser: " + ex.Message);
        }

        LaunchSystemChooserFallback(completion);
    }

    /// <summary>System gallery/camera chooser fallback (used only when the in-app scanner cannot start).</summary>
    private void LaunchSystemChooserFallback(TaskCompletionSource<string?> completion)
    {
        try
        {
            var gallery = new Intent(Intent.ActionGetContent).SetType("image/*");
            gallery?.AddCategory(Intent.CategoryOpenable);
            var camera = TryBuildCameraCaptureIntent();

            if (camera is not null)
            {
                var chooser = Intent.CreateChooser(gallery, "扫描配对二维码");
                chooser?.PutExtra(
                    Intent.ExtraInitialIntents,
                    new global::Android.OS.IParcelable[] { camera });
                _qrScanLauncher!.Launch(chooser);
                global::Android.Util.Log.Info(Tag, "[QrScan] launched system chooser (gallery + camera)");
                return;
            }

            if (gallery is not null)
            {
                _qrScanLauncher!.Launch(gallery);
                global::Android.Util.Log.Info(Tag, "[QrScan] launched gallery picker");
                return;
            }

            throw new InvalidOperationException("no scanner intent available");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] chooser failed, falling back: " + ex.Message);
            TryLaunchCameraFallback(completion);
        }
    }

    /// <summary>Builds ACTION_IMAGE_CAPTURE writing into the app cache through its FileProvider.</summary>
    private Intent? TryBuildCameraCaptureIntent()
    {
        try
        {
            var cacheDir = CacheDir;
            if (cacheDir is null)
            {
                return null;
            }

            var file = new Java.IO.File(
                cacheDir,
                "qr_scan_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".jpg");
            var authority = PackageName + ".fileprovider";
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(this, authority, file);
            _qrCaptureUri = uri;

            var intent = new Intent(MediaStore.ActionImageCapture);
            intent.PutExtra(MediaStore.ExtraOutput, uri);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            // Android 7+ camera apps receive the write grant through both flags and ClipData; some OEM camera
            // apps (ColorOS) only honour the ClipData variant, which used to make the chooser camera do nothing.
            intent.ClipData = global::Android.Content.ClipData.NewRawUri("qr-capture", uri);

            var resolved = PackageManager?.ResolveActivity(intent, PackageInfoFlags.MatchDefaultOnly);
            var cameraPackage = resolved?.ActivityInfo?.PackageName;
            if (string.IsNullOrWhiteSpace(cameraPackage))
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] no camera app resolved for ACTION_IMAGE_CAPTURE");
                return null;
            }

            GrantUriPermission(
                cameraPackage,
                uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] camera capture uri={uri} cameraPackage={cameraPackage} file={file.Name}");
            return intent;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] camera intent unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>Last-resort camera path: needs the declared CAMERA permission, requested at runtime here.</summary>
    private void TryLaunchCameraFallback(TaskCompletionSource<string?> completion)
    {
        var camera = TryBuildCameraCaptureIntent();
        if (camera is null)
        {
            FailQrScan(completion);
            return;
        }

        EnsureCameraPermissionAsync().ContinueWith(
            task =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!task.Result)
                    {
                        global::Android.Util.Log.Warn(Tag, "[QrScan] CAMERA permission denied; scan aborted");
                        FailQrScan(completion);
                        return;
                    }

                    try
                    {
                        _qrScanLauncher!.Launch(camera);
                        global::Android.Util.Log.Info(Tag, "[QrScan] launched direct camera capture");
                    }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn(Tag, "[QrScan] direct camera failed: " + ex.Message);
                        FailQrScan(completion);
                    }
                });
            },
            TaskScheduler.Default);
    }

    private Task<bool> EnsureCameraPermissionAsync()
    {
        const string permission = "android.permission.CAMERA";
        if (CheckSelfPermission(permission) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_qrScanSync)
        {
            _cameraPermissionCompletion = completion;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RequestPermissions(new[] { permission }, CameraPermissionRequestCode);
                global::Android.Util.Log.Info(Tag, "[QrScan] requested CAMERA permission");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] CAMERA request failed: " + ex.Message);
                completion.TrySetResult(false);
            }
        });
        return completion.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode == CameraPermissionRequestCode)
        {
            TaskCompletionSource<bool>? completion;
            lock (_qrScanSync)
            {
                completion = _cameraPermissionCompletion;
                _cameraPermissionCompletion = null;
            }

            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            global::Android.Util.Log.Info(Tag, $"[QrScan] CAMERA permission granted={granted}");
            completion?.TrySetResult(granted);
        }

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    private void OnQrScanResult(AndroidX.Activity.Result.ActivityResult? result)
    {
        TaskCompletionSource<string?>? completion;
        lock (_qrScanSync)
        {
            completion = _pendingQrScan;
            _pendingQrScan = null;
        }

        if (completion is null)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] result with no pending scan (canceled earlier?)");
            return;
        }

        var resultCode = result?.ResultCode ?? (int)Result.Canceled;
        var data = result?.Data;

        // In-app camera scanner returns the decoded text directly; system-chooser fallback returns an image URI.
        var scannedText = data?.GetStringExtra(QrScannerActivity.ExtraResult);
        if (resultCode == (int)Result.Ok && !string.IsNullOrEmpty(scannedText))
        {
            global::Android.Util.Log.Info(Tag, "[QrScan] in-app scanner returned " + AndroidQrScanner.Describe(scannedText));
            _qrCaptureUri = null;
            _qrScannerDebugImage = null;
            completion.TrySetResult(scannedText);
            return;
        }

        var uri = data?.Data ?? _qrCaptureUri;
        _qrCaptureUri = null;
        _qrScannerDebugImage = null;

        if (resultCode != (int)Result.Ok || uri is null)
        {
            global::Android.Util.Log.Info(Tag, $"[QrScan] canceled or no image (resultCode={resultCode})");
            completion.TrySetResult(null);
            return;
        }

        global::Android.Util.Log.Info(Tag, $"[QrScan] image selected: {uri}");
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await AndroidQrScanner
                    .DecodeUriAsync(this, uri, CancellationToken.None)
                    .ConfigureAwait(false);
                completion.TrySetResult(text);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] decode failed: " + ex.Message);
                completion.TrySetResult(null);
            }
        });
    }

    private void FailQrScan(TaskCompletionSource<string?> completion)
    {
        lock (_qrScanSync)
        {
            if (ReferenceEquals(_pendingQrScan, completion))
            {
                _pendingQrScan = null;
            }
        }

        _qrCaptureUri = null;
        completion.TrySetResult(null);
    }

    private sealed class QrScanCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _activity;

        public QrScanCallback(MainActivity activity) => _activity = activity;

        public void OnActivityResult(Java.Lang.Object? result)
            => _activity.OnQrScanResult(result as AndroidX.Activity.Result.ActivityResult);
    }

    /// <summary>
    /// adb diagnostics for M6: runs <see cref="PlatformServices.ScanQrAsync"/> end-to-end and logs a redacted
    /// description (length/prefix/CJK), plus match=True/False when an expected text is supplied.
    /// </summary>
    private async void RunQrSelfTest(string? expected)
    {
        try
        {
            global::Android.Util.Log.Info(Tag, "[QrScan] self-test start");
            var text = await PlatformServices.ScanQrAsync().ConfigureAwait(false);
            var match = expected is null
                ? string.Empty
                : $" match={string.Equals(text, expected, StringComparison.Ordinal)}";
            global::Android.Util.Log.Info(Tag, "[QrScan] self-test decoded " + AndroidQrScanner.Describe(text) + match);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[QrScan] self-test failed: " + ex);
        }
    }

    /// <summary>The real camera/gallery chooser can only be launched once the activity is RESUMED.</summary>
    private void ScheduleQrScanUiSelfTest()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2_000).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                AndroidQrScanner.DebugImagePath = null;
                RunQrSelfTest(expected: null);
            });
        });
    }

    private sealed class BackPressedCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;

        public BackPressedCallback(MainActivity activity)
            : base(true)
        {
            _activity = activity;
        }

        public override void HandleOnBackPressed() => _activity.HandleBackInvoked();
    }

    protected override void OnResume()
    {
        base.OnResume();
        PlatformServices.AppForeground = true;
        global::Android.Util.Log.Info(Tag, "[Lifecycle] resumed appForeground=true");
        RefreshLayoutInsets();
    }

    protected override void OnPause()
    {
        PlatformServices.AppForeground = false;
        global::Android.Util.Log.Info(Tag, "[Lifecycle] paused appForeground=false");
        base.OnPause();
    }

    /// <summary>Back fallback for APIs that still call <c>OnBackPressed</c> directly (pre-Android 13).</summary>
#pragma warning disable CS0672, CS0618, CA1422 // OnBackPressed is deprecated since API 33; kept for older devices
    public override void OnBackPressed() => HandleBackInvoked();
#pragma warning restore CS0672, CS0618, CA1422

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is null)
        {
            return;
        }

        HandleIntent(intent, "new-intent");
    }

    protected override void OnDestroy()
    {
        // A finishing activity must not kill a healthy background connection; the shared layer calls
        // PlatformServices.SetConnectionActive(false) when the connection is really gone.
        var stopKeepAlive = IsFinishing && !IsChangingConfigurations && !PlatformServices.ConnectionActive;
        global::Android.Util.Log.Info(
            Tag,
            $"OnDestroy finishing={IsFinishing} changingConfig={IsChangingConfigurations} " +
            $"connectionActive={PlatformServices.ConnectionActive} stopKeepAlive={stopKeepAlive}");

        RemoveInsetsTracking();
        UninstallBackHandling();
        lock (_imagePickSync)
        {
            _pendingImagePick?.TrySetResult(null);
            _pendingImagePick = null;
        }

        lock (_qrScanSync)
        {
            _pendingQrScan?.TrySetResult(null);
            _pendingQrScan = null;
            _cameraPermissionCompletion?.TrySetResult(false);
            _cameraPermissionCompletion = null;
        }

        PlatformServices.AppForeground = false;
        if (stopKeepAlive)
        {
            PlatformServices.StopForegroundConnection?.Invoke();
        }

        base.OnDestroy();
    }

    private void InstallInsetsTracking()
    {
        try
        {
            _decorView = Window?.DecorView;
            if (_decorView is null)
            {
                return;
            }

            _insetsListener = new InsetsLayoutListener(this);
            _decorView.ViewTreeObserver?.AddOnGlobalLayoutListener(_insetsListener);
            RefreshLayoutInsets();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "insets tracking failed: " + ex.Message);
        }
    }

    private void RemoveInsetsTracking()    {
        try
        {
            if (_decorView is not null && _insetsListener is not null)
            {
                _decorView.ViewTreeObserver?.RemoveOnGlobalLayoutListener(_insetsListener);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "insets listener removal failed: " + ex.Message);
        }
        finally
        {
            _insetsListener = null;
            _decorView = null;
        }
    }

    /// <summary>
    /// Publishes system-bar / cutout / IME insets in DIPs to <see cref="PlatformServices"/>. Called from the
    /// decor view's global layout pass (which also fires when the window is resized by the keyboard) and on
    /// resume. Uses <c>WindowInsetsCompat</c> so the same code path works from API 24 to 36.
    /// </summary>
    private void RefreshLayoutInsets()
    {
        try
        {
            var decor = _decorView ?? Window?.DecorView;
            if (decor is null)
            {
                return;
            }

            var density = Resources?.DisplayMetrics?.Density ?? 1.0;
            if (density <= 0)
            {
                density = 1.0;
            }

            double left = 0, top = 0, right = 0, bottom = 0, ime = 0;
            var compat = ViewCompat.GetRootWindowInsets(decor);
            if (compat is not null)
            {
                var barsType = WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout();
                var bars = compat.GetInsets(barsType);
                if (bars is not null)
                {
                    left = bars.Left / density;
                    top = bars.Top / density;
                    right = bars.Right / density;
                    bottom = bars.Bottom / density;
                }

                var imeInsets = compat.GetInsets(WindowInsetsCompat.Type.Ime());
                if (imeInsets is not null)
                {
                    ime = Math.Max(0, imeInsets.Bottom / density);
                }
            }

            PlatformServices.SetLayoutInsets(new Avalonia.Thickness(left, top, right, bottom), ime);

            var signature = $"{left:F1},{top:F1},{right:F1},{bottom:F1}|{ime:F1}";
            if (!string.Equals(signature, _lastInsetsSignature, StringComparison.Ordinal))
            {
                _lastInsetsSignature = signature;
                global::Android.Util.Log.Info(
                    Tag,
                    $"[Insets] safe=({left:F1},{top:F1},{right:F1},{bottom:F1})dp ime={ime:F1}dp");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "insets refresh failed: " + ex.Message);
        }
    }

    private sealed class InsetsLayoutListener : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
    {
        private readonly MainActivity _activity;

        public InsetsLayoutListener(MainActivity activity) => _activity = activity;

        public void OnGlobalLayout() => _activity.RefreshLayoutInsets();
    }

    private void HandleIntent(Intent? intent, string source)
    {
        if (intent is null)
        {
            return;
        }

        try
        {
            var data = intent.Data;
            if (data is not null &&
                string.Equals(data.Scheme, DeepLinkScheme, StringComparison.OrdinalIgnoreCase))
            {
                var uri = data.ToString() ?? string.Empty;
                global::Android.Util.Log.Info(Tag, $"[DeepLink] source={source} uri={PlatformServices.Redact(uri)}");
                PlatformServices.DispatchDeepLink(uri, source);
            }

            var sessionId = intent.GetStringExtra(ExtraSessionId);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                global::Android.Util.Log.Info(Tag, $"[NotificationTap] sessionId={sessionId}");
                PlatformServices.DispatchSessionOpen(sessionId, source);
            }

            if (intent.GetBooleanExtra(ExtraStartForegroundService, false))
            {
                global::Android.Util.Log.Info(Tag, "[Diagnostics] foreground connection start scheduled");
                ScheduleDiagnosticForegroundStart();
            }

            if (intent.GetBooleanExtra(ExtraStopForegroundService, false))
            {
                global::Android.Util.Log.Info(Tag, "[Diagnostics] stopping foreground connection");
                PlatformServices.SetConnectionActive(false);
            }

            var notifyTest = intent.GetStringExtra(ExtraNotifyTest);
            if (!string.IsNullOrWhiteSpace(notifyTest))
            {
                PostDiagnosticNotification(notifyTest!, sessionId);
            }

            var keystoreTest = intent.GetStringExtra(ExtraKeystoreTest);
            if (!string.IsNullOrWhiteSpace(keystoreTest))
            {
                RunKeystoreSelfTest(keystoreTest!);
            }

            // M5 diagnostics: file-path pick (no UI, for emulator automation) or open the real picker UI.
            var imagePath = intent.GetStringExtra(ExtraImagePath);
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                AndroidImagePicker.DebugImagePath = imagePath;
                global::Android.Util.Log.Info(Tag, "[ImagePicker] debug image path from intent: " + imagePath);
                RunImagePickerSelfTest(useDebugPath: true);
            }
            else if (intent.GetBooleanExtra(ExtraImagePickUi, false))
            {
                ScheduleImagePickerUiSelfTest();
            }

            // M6 diagnostics: decode a saved QR image (no UI) or open the real camera/gallery scanner.
            var qrImagePath = intent.GetStringExtra(AndroidQrScanner.DebugImagePathExtra);
            var qrDebugImage = intent.GetStringExtra(QrScannerActivity.ExtraDebugImage);
            if (!string.IsNullOrWhiteSpace(qrImagePath))
            {
                AndroidQrScanner.DebugImagePath = qrImagePath;
                global::Android.Util.Log.Info(Tag, "[QrScan] debug image path from intent: " + qrImagePath);
                RunQrSelfTest(intent.GetStringExtra(AndroidQrScanner.DebugExpectExtra));
            }
            else if (!string.IsNullOrWhiteSpace(qrDebugImage))
            {
                // Drives the in-app scanner Activity end-to-end without a physical QR in the camera view.
                _qrScannerDebugImage = qrDebugImage;
                global::Android.Util.Log.Info(Tag, "[QrScan] scanner debug image from intent: " + qrDebugImage);
                ScheduleQrScanUiSelfTest();
            }
            else if (intent.GetBooleanExtra(AndroidQrScanner.ScanUiExtra, false))
            {
                ScheduleQrScanUiSelfTest();
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "HandleIntent failed: " + ex);
        }
    }

    /// <summary>
    /// Verification hook: start the foreground service a few seconds after the intent is handled. The shared
    /// layer pushes <c>SetConnectionActive(false)</c> while it initialises (no connection yet), so starting the
    /// service during <c>OnCreate</c> would race with that stop; production starts it when a connection opens.
    /// </summary>
    private static void ScheduleDiagnosticForegroundStart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(4_000).ConfigureAwait(false);
            var started = PlatformServices.SetConnectionActive(true);
            global::Android.Util.Log.Info(Tag, $"[Diagnostics] foreground connection start invoked ok={started}");
        });
    }

    /// <summary>
    /// Diagnostics for the Keystore-backed <see cref="ISecretStore"/>: <c>store</c> writes a random value and
    /// reads it back; <c>check</c> (after a process restart) verifies the value is still decryptable, which
    /// proves the AES/GCM key survived in the Android Keystore and only the ciphertext is on disk.
    /// </summary>
    private async void RunKeystoreSelfTest(string mode)
    {
        const string key = "diagnostics.keystore-roundtrip";
        try
        {
            var store = PlatformServices.SecretStore;
            if (store is null)
            {
                global::Android.Util.Log.Error(Tag, "[KeystoreSelfTest] no ISecretStore registered");
                return;
            }

            if (string.Equals(mode, "store", StringComparison.OrdinalIgnoreCase))
            {
                var value = "dsh-" + Guid.NewGuid().ToString("N") + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await store.SetAsync(key, value).ConfigureAwait(false);
                var read = await store.GetAsync(key).ConfigureAwait(false);
                var ok = string.Equals(read, value, StringComparison.Ordinal);
                global::Android.Util.Log.Info(Tag, $"[KeystoreSelfTest] store ok={ok} length={value.Length}");
            }
            else
            {
                var read = await store.GetAsync(key).ConfigureAwait(false);
                var present = !string.IsNullOrEmpty(read);
                global::Android.Util.Log.Info(
                    Tag,
                    $"[KeystoreSelfTest] check present={present} length={(read?.Length ?? 0)}");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[KeystoreSelfTest] failed: " + ex);
        }
    }

    private static void PostDiagnosticNotification(string kind, string? sessionId)
    {
        switch (kind.ToLowerInvariant())
        {
            case "approval":
                NotificationBridge.NotifyApprovalNeeded("需要审批", "bash 请求执行: npm install（诊断通知）", sessionId);
                break;
            case "question":
                NotificationBridge.NotifyQuestionNeeded("需要回答", "选择继续方式：继续 / 停止（诊断通知）", sessionId);
                break;
            default:
                NotificationBridge.NotifyTurnFinished("任务完成", "回合已结束（诊断通知）");
                break;
        }

        global::Android.Util.Log.Info(Tag, $"[Diagnostics] notification kind={kind} posted");
    }
}

[Application]
public class DshRemoteAndroidApplication : AvaloniaAndroidApplication<global::DshRemote.App.App>
{
    protected DshRemoteAndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // Managed exceptions that escape any thread still go to the shared rolling diagnostics log, so a
        // crash report can be pulled with adb without a debugger.
        try
        {
            AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
                DiagnosticsLog.Record("ANDROID-UNHANDLED", args.Exception);
        }
        catch
        {
            // ignored
        }

        // The shared App project never references Android; the head injects the platform pieces.
        AppHost.StateDirectoryProvider = () =>
        {
            try
            {
                return global::Android.App.Application.Context.FilesDir?.AbsolutePath;
            }
            catch
            {
                return null;
            }
        };

        // Lead pushes the desktop pairing file here with:
        //   adb push %USERPROFILE%\.dsh\remote\pairing.json \
        //       /sdcard/Android/data/dev.dshremote.app/files/pairing.json
        // (app-specific external dir, no storage permission required).
        AppHost.PairingFilePathsProvider = () =>
        {
            var candidates = new List<string>(4);
            try
            {
                var external = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrWhiteSpace(external))
                {
                    candidates.Add(Path.Combine(external, "pairing.json"));
                    candidates.Add(Path.Combine(external, "pairing.txt"));
                }
            }
            catch
            {
                // ignored: external storage can be unavailable while the device is locked
            }

            try
            {
                var internalDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
                if (!string.IsNullOrWhiteSpace(internalDir))
                {
                    candidates.Add(Path.Combine(internalDir, "pairing.json"));
                    candidates.Add(Path.Combine(internalDir, "pairing.txt"));
                }
            }
            catch
            {
                // ignored
            }

            return candidates;
        };

        // M3: notification channels + Keystore secret store + foreground-service hooks must exist before
        // Avalonia creates the shared App / view models.
        Platform.AndroidPlatformHost.Initialize(this);

        base.OnCreate();
    }
}


