using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using DshRemote.App.Platform;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// M6 in-app QR scanner: Camera2 preview (TextureView) + an ImageReader on the YUV_420_888 stream whose
/// luminance plane is decoded frame-by-frame with ZXing.Net (no GMS/CameraX dependency). Returns the decoded
/// text through <see cref="ExtraResult"/>; cancelling (back / no permission) returns RESULT_CANCELED.
/// The gallery button reuses the existing content-URI decode path.
/// </summary>
[Activity(
    Label = "扫描二维码",
    Theme = "@style/QrScannerTheme",
    Exported = false,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class QrScannerActivity : Activity
{
    /// <summary>Result extra carrying the decoded QR text.</summary>
    public const string ExtraResult = "dshremote.qr.result";

    /// <summary>Diagnostics: decode this file instead of waiting for a camera frame (emulator E2E).</summary>
    public const string ExtraDebugImage = "dshremote.qrDebugImage";

    private const string Tag = "DshRemoteQr";
    private const int CameraPermissionRequestCode = 0x4455;
    private const int GalleryRequestCode = 0x4456;
    private const int LensFacingBack = 1;
    private const int DecodeThrottleMs = 220;
    private const int TargetDecodeWidth = 1280;
    private const int TargetDecodeHeight = 720;

    private static bool _loggedPlaneLayout;

    private readonly object _cameraSync = new();

    private TextureView? _preview;
    private TextView? _status;
    private Button? _torchButton;
    private CameraManager? _cameraManager;
    private string? _cameraId;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private ImageReader? _imageReader;
    private Surface? _previewSurface;
    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private bool _started;
    private bool _delivered;
    private bool _torchOn;
    private bool _flashAvailable;
    private bool _textureReady;
    private long _lastDecodeTicks;
    private int _frameCount;

    private global::Android.Window.IOnBackInvokedCallback? _backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The dark system bars come from @style/QrScannerTheme (avoids APIs deprecated in Android 15+).
        BuildUi();
        InstallBackCallback();

        _cameraThread = new HandlerThread("dsh-qr-camera");
        _cameraThread.Start();
        _cameraHandler = new Handler(_cameraThread.Looper!);

        var debugImage = Intent?.GetStringExtra(ExtraDebugImage);
        if (!string.IsNullOrWhiteSpace(debugImage))
        {
            global::Android.Util.Log.Info(Tag, "[QrScan] scanner debug image: " + debugImage);
            _ = Task.Run(() =>
            {
                string? text = null;
                try
                {
                    var bytes = File.ReadAllBytes(debugImage!);
                    text = QrCodeDecoder.DecodeImageBytes(bytes);
                    // Prove the Camera2 YUV luminance decode path with real QR content (not just the Bitmap path).
                    var luminanceOk = QrCodeDecoder.VerifyLuminancePath(bytes, out var lw, out var lh);
                    global::Android.Util.Log.Info(
                        Tag,
                        $"[QrScan] luminance path ok={luminanceOk} dims={lw}x{lh}");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn(Tag, "[QrScan] scanner debug decode failed: " + ex.Message);
                }

                RunOnUiThread(() => Deliver(text));
            });
            return;
        }

        ResolveBackCamera();
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            TryStartCamera();
        }
        else
        {
            RequestPermissions(new[] { global::Android.Manifest.Permission.Camera }, CameraPermissionRequestCode);
            UpdateStatus("需要相机权限才能扫码");
        }
    }

    // ---- UI ---------------------------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);

        _preview = new TextureView(this);
        _preview.SurfaceTextureListener = new PreviewListener(this);
        root.AddView(_preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var overlay = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        root.AddView(overlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var top = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        top.SetPadding(Dp(8), Dp(40), Dp(8), Dp(10));
        top.SetBackgroundColor(Color.Argb(150, 0, 0, 0));

        var back = CreateFlatButton("‹ 返回");
        back.Click += (_, _) => Deliver(null);
        top.AddView(back, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var title = new TextView(this) { Text = "扫描二维码" };
        title.SetTextColor(Color.White);
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 17f);
        title.SetTypeface(null, TypefaceStyle.Bold);
        var titleParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        titleParams.LeftMargin = Dp(8);
        top.AddView(title, titleParams);

        _torchButton = CreateFlatButton("手电筒");
        _torchButton.Click += (_, _) => ToggleTorch();
        _torchButton.Enabled = false;
        top.AddView(_torchButton, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        overlay.AddView(top, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        overlay.AddView(new View(this), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _status = new TextView(this) { Text = "将二维码放入取景框内" };
        _status.SetTextColor(Color.White);
        _status.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        _status.Gravity = GravityFlags.Center;
        _status.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
        _status.SetBackgroundColor(Color.Argb(130, 0, 0, 0));
        overlay.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        overlay.AddView(new View(this), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var bottom = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        bottom.SetGravity(GravityFlags.Center);
        bottom.SetPadding(Dp(16), Dp(14), Dp(16), Dp(32));
        bottom.SetBackgroundColor(Color.Argb(150, 0, 0, 0));

        var hint = new TextView(this) { Text = "相机不可用或识别失败时，可从相册选择二维码图片" };
        hint.SetTextColor(Color.ParseColor("#B8BDD4"));
        hint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        hint.Gravity = GravityFlags.Center;
        bottom.AddView(hint, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var gallery = CreateFlatButton("从相册选择");
        gallery.Click += (_, _) => PickGalleryImage();
        var galleryParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        galleryParams.TopMargin = Dp(12);
        bottom.AddView(gallery, galleryParams);

        overlay.AddView(bottom, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SetContentView(root);
    }

    private Button CreateFlatButton(string text)
    {
        var button = new Button(this) { Text = text };
        button.SetTextColor(Color.White);
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        button.SetAllCaps(false);
        button.SetBackgroundColor(Color.Argb(60, 255, 255, 255));
        button.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
        return button;
    }

    private int Dp(int value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1f));

    private void UpdateStatus(string text)
    {
        RunOnUiThread(() =>
        {
            if (_status is not null)
            {
                _status.Text = text;
            }
        });
    }

    // ---- back / lifecycle -------------------------------------------------------------------------

    private void InstallBackCallback()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        InstallBackCallbackCore();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private void InstallBackCallbackCore()
    {
        try
        {
            var dispatcher = OnBackInvokedDispatcher;
            if (dispatcher is null)
            {
                return;
            }

            _backCallback = new BackCallback(this);
            dispatcher.RegisterOnBackInvokedCallback(0, _backCallback);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] scanner back callback failed: " + ex.Message);
        }
    }

    private sealed class BackCallback : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        private readonly QrScannerActivity _activity;

        public BackCallback(QrScannerActivity activity) => _activity = activity;

        public void OnBackInvoked() => _activity.Deliver(null);
    }

#pragma warning disable CS0672, CS0618, CA1422 // pre-Android-13 fallback
    public override void OnBackPressed() => Deliver(null);
#pragma warning restore CS0672, CS0618, CA1422

    protected override void OnResume()
    {
        base.OnResume();
        if (!_delivered && !string.IsNullOrWhiteSpace(Intent?.GetStringExtra(ExtraDebugImage)))
        {
            return;
        }

        if (!_delivered && CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            TryStartCamera();
        }
    }

    protected override void OnPause()
    {
        if (!_delivered)
        {
            StopCamera();
        }

        base.OnPause();
    }

    protected override void OnDestroy()
    {
        StopCamera();
        _cameraThread?.QuitSafely();
        _cameraThread = null;
        _cameraHandler = null;
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode == CameraPermissionRequestCode)
        {
            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            global::Android.Util.Log.Info(Tag, $"[QrScan] camera permission granted={granted}");
            if (granted)
            {
                TryStartCamera();
            }
            else
            {
                Toast.MakeText(this, "未获得相机权限，可从相册选择二维码", ToastLength.Long)?.Show();
                Deliver(null);
            }
        }

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    // ---- camera ------------------------------------------------------------------------------------

    private void ResolveBackCamera()
    {
        try
        {
            _cameraManager = (CameraManager?)GetSystemService(CameraService);
            if (_cameraManager is null)
            {
                UpdateStatus("相机服务不可用");
                return;
            }

            var ids = _cameraManager.GetCameraIdList() ?? Array.Empty<string>();
            foreach (var id in ids)
            {
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(id);
                    var facing = characteristics?.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                    if (facing?.IntValue() == LensFacingBack)
                    {
                        _cameraId = id;
                        var flash = characteristics?.Get(CameraCharacteristics.FlashInfoAvailable) as Java.Lang.Boolean;
                        _flashAvailable = flash?.BooleanValue() == true;
                        break;
                    }
                }
                catch
                {
                    // some logical cameras throw; skip them
                }
            }

            _cameraId ??= ids.FirstOrDefault();
            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] scanner camera id={_cameraId ?? "-"} flash={_flashAvailable}");
            if (_torchButton is not null)
            {
                _torchButton.Enabled = _flashAvailable && !string.IsNullOrEmpty(_cameraId);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[QrScan] camera enumeration failed: " + ex);
        }
    }

    internal void OnPreviewSurfaceAvailable()
    {
        _textureReady = true;
        TryStartCamera();
    }

    private void TryStartCamera()
    {
        lock (_cameraSync)
        {
            if (_delivered || _started || !_textureReady || _cameraHandler is null ||
                string.IsNullOrEmpty(_cameraId) || _preview?.IsAvailable != true)
            {
                return;
            }

            if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
            {
                RequestPermissions(new[] { global::Android.Manifest.Permission.Camera }, CameraPermissionRequestCode);
                return;
            }

            if (_cameraManager is null)
            {
                _cameraManager = (CameraManager?)GetSystemService(CameraService);
            }

            if (_cameraManager is null)
            {
                UpdateStatus("相机服务不可用");
                return;
            }

            try
            {
                _started = true;
                var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId!);
                var map = characteristics?.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;
                var (width, height) = ChooseDecodeSize(map);

                _preview!.SurfaceTexture?.SetDefaultBufferSize(width, height);
                _previewSurface = new Surface(_preview.SurfaceTexture);
                _imageReader = ImageReader.NewInstance(width, height, global::Android.Graphics.ImageFormatType.Yuv420888, 2);
                _imageReader.SetOnImageAvailableListener(new FrameListener(this), _cameraHandler);

                _cameraManager.OpenCamera(_cameraId!, new DeviceCallback(this), _cameraHandler);
                global::Android.Util.Log.Info(Tag, $"[QrScan] scanner camera opening size={width}x{height}");
            }
            catch (Exception ex)
            {
                _started = false;
                global::Android.Util.Log.Error(Tag, "[QrScan] scanner camera start failed: " + ex);
                UpdateStatus("相机打开失败，可从相册选择二维码");
                StopCamera();
            }
        }
    }

    private static (int Width, int Height) ChooseDecodeSize(StreamConfigurationMap? map)
    {
        try
        {
            var sizes = map?.GetOutputSizes((int)global::Android.Graphics.ImageFormatType.Yuv420888);
            if (sizes is null || sizes.Length == 0)
            {
                return (TargetDecodeWidth, TargetDecodeHeight);
            }

            var best = sizes[0];
            var bestScore = int.MaxValue;
            foreach (var size in sizes)
            {
                if (size is null)
                {
                    continue;
                }

                // Prefer a landscape size close to 1280x720 but never larger than ~1080p (memory/frame time).
                var pixels = size.Width * size.Height;
                if (pixels > 1920 * 1080 || pixels < 640 * 480)
                {
                    continue;
                }

                var score = Math.Abs(size.Width - TargetDecodeWidth) + Math.Abs(size.Height - TargetDecodeHeight);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = size;
                }
            }

            var width = Math.Max(best.Width, best.Height);
            var height = Math.Min(best.Width, best.Height);
            return (width, height);
        }
        catch
        {
            return (TargetDecodeWidth, TargetDecodeHeight);
        }
    }

    internal void OnCameraOpened(CameraDevice device)
    {
        try
        {
            _cameraDevice = device;
            var surfaces = new List<Surface>(2);
            if (_previewSurface is not null)
            {
                surfaces.Add(_previewSurface);
            }

            if (_imageReader?.Surface is not null)
            {
                surfaces.Add(_imageReader.Surface);
            }

#pragma warning disable CA1422 // deprecated on API 30+ but still functional on 24-36 and avoids SessionConfiguration plumbing
            device.CreateCaptureSession(surfaces, new SessionCallback(this), _cameraHandler);
#pragma warning restore CA1422
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[QrScan] scanner session create failed: " + ex);
            UpdateStatus("相机初始化失败，可从相册选择二维码");
        }
    }

    internal void OnCameraDisconnected()
    {
        global::Android.Util.Log.Warn(Tag, "[QrScan] scanner camera disconnected");
        UpdateStatus("相机已断开，可从相册选择二维码");
    }

    internal void OnCameraError(string? message)
    {
        global::Android.Util.Log.Error(Tag, "[QrScan] scanner camera error: " + message);
        UpdateStatus("相机出错，可从相册选择二维码");
    }

    internal void OnSessionConfigured(CameraCaptureSession session)
    {
        try
        {
            _captureSession = session;
            ApplyRepeatingRequest();
            UpdateStatus("将二维码放入取景框内");
            global::Android.Util.Log.Info(Tag, "[QrScan] scanner preview session configured");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error(Tag, "[QrScan] scanner repeating request failed: " + ex);
        }
    }

    /// <summary>
    /// (Re)builds the repeating preview request. Used for both the initial session and the torch toggle: while
    /// our own CameraDevice owns the camera, CameraManager.SetTorchMode fails with CAMERA_IN_USE, so the torch
    /// must be driven through CONTROL_AE_MODE + FLASH_MODE on the repeating capture request instead.
    /// </summary>
    private void ApplyRepeatingRequest()
    {
        if (_captureSession is null || _cameraDevice is null)
        {
            return;
        }

        var request = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
        if (_previewSurface is not null)
        {
            request.AddTarget(_previewSurface);
        }

        if (_imageReader?.Surface is not null)
        {
            request.AddTarget(_imageReader.Surface);
        }

        try
        {
            // CONTROL_AF_MODE_CONTINUOUS_PICTURE = 4, CONTROL_AE_MODE_ON = 1, FLASH_MODE_TORCH = 2 / OFF = 0.
            request.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf(4));
            request.Set(CaptureRequest.ControlAeMode!, Java.Lang.Integer.ValueOf(1));
            request.Set(CaptureRequest.FlashMode!, Java.Lang.Integer.ValueOf(_torchOn ? 2 : 0));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] scanner request control failed: " + ex.Message);
        }

        _captureSession.SetRepeatingRequest(request.Build(), null, _cameraHandler);
        global::Android.Util.Log.Info(Tag, $"[QrScan] scanner repeating request updated torch={_torchOn}");
    }

    internal void OnFrameAvailable(ImageReader reader)
    {
        // Throttle before acquiring: the camera keeps producing frames while ZXing decodes, and holding more
        // than maxImages images makes AcquireLatestImage throw ("maxImages ... already been acquired").
        var now = System.Environment.TickCount64;
        if (_delivered || now - _lastDecodeTicks < DecodeThrottleMs)
        {
            return;
        }

        _lastDecodeTicks = now;

        Image? image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null)
            {
                return;
            }

            _frameCount++;
            var luminance = ExtractLuminance(image, out var width, out var height);
            if (luminance is null)
            {
                return;
            }

            var text = QrCodeDecoder.DecodeLuminance(luminance, width, height);
            if (string.IsNullOrEmpty(text))
            {
                if (_frameCount == 1 || _frameCount % 25 == 0)
                {
                    global::Android.Util.Log.Info(
                        Tag,
                        $"[QrScan] scanner frames={_frameCount} no QR yet ({width}x{height})");
                }

                return;
            }

            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] scanner frame decode ok frames={_frameCount} {AndroidQrScanner.Describe(text)}");
            RunOnUiThread(() => Deliver(text));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] scanner frame decode failed: " + ex.Message);
        }
        finally
        {
            // Always close the camera image, even on the throttled/exception paths.
            try
            {
                image?.Close();
            }
            catch
            {
                // ignore double-close
            }

            image?.Dispose();
        }
    }

    private static byte[]? ExtractLuminance(Image image, out int width, out int height)
    {
        width = image.Width;
        height = image.Height;
        var planes = image.GetPlanes();
        if (planes is null || planes.Length == 0)
        {
            return null;
        }

        var plane = planes[0];
        var buffer = plane?.Buffer;
        if (buffer is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var rowStride = plane!.RowStride;
        if (!_loggedPlaneLayout)
        {
            _loggedPlaneLayout = true;
            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] frame plane rowStride={rowStride} pixelStride={plane.PixelStride} size={width}x{height}");
        }

        var luminance = new byte[width * height];
        var row = new byte[Math.Max(rowStride, width)];
        try
        {
            buffer.Rewind();
            for (var y = 0; y < height; y++)
            {
                var remaining = buffer.Remaining();
                if (remaining <= 0)
                {
                    break;
                }

                var toRead = Math.Min(rowStride, remaining);
                buffer.Get(row, 0, toRead);
                Array.Copy(row, 0, luminance, y * width, Math.Min(width, toRead));
            }

            return luminance;
        }
        catch
        {
            return null;
        }
    }

    private void ToggleTorch()
    {
        try
        {
            if (!_flashAvailable)
            {
                Toast.MakeText(this, "该设备不支持手电筒", ToastLength.Short)?.Show();
                return;
            }

            _torchOn = !_torchOn;
            if (_torchButton is not null)
            {
                _torchButton.Text = _torchOn ? "关闭手电" : "手电筒";
            }

            if (_captureSession is not null && _cameraDevice is not null)
            {
                ApplyRepeatingRequest();
            }
            else if (_cameraManager is not null && !string.IsNullOrEmpty(_cameraId))
            {
                // Camera not open yet: the manager-level torch is allowed here.
                _cameraManager.SetTorchMode(_cameraId, _torchOn);
            }

            global::Android.Util.Log.Info(Tag, $"[QrScan] scanner torch={_torchOn}");
        }
        catch (Exception ex)
        {
            _torchOn = false;
            if (_torchButton is not null)
            {
                _torchButton.Text = "手电筒";
            }

            global::Android.Util.Log.Warn(Tag, "[QrScan] torch unavailable: " + ex.Message);
            Toast.MakeText(this, "手电筒不可用", ToastLength.Short)?.Show();
        }
    }

    private void StopCamera()
    {
        lock (_cameraSync)
        {
            if (_torchOn)
            {
                try
                {
                    _cameraManager?.SetTorchMode(_cameraId!, false);
                }
                catch
                {
                    // ignore
                }

                _torchOn = false;
            }

            try
            {
                _captureSession?.Close();
            }
            catch
            {
                // ignore
            }

            _captureSession = null;
            try
            {
                _cameraDevice?.Close();
            }
            catch
            {
                // ignore
            }

            _cameraDevice = null;
            try
            {
                _imageReader?.Close();
            }
            catch
            {
                // ignore
            }

            _imageReader = null;
            try
            {
                _previewSurface?.Release();
            }
            catch
            {
                // ignore
            }

            _previewSurface = null;
            _started = false;
        }
    }

    // ---- gallery fallback / result -----------------------------------------------------------------

    private void PickGalleryImage()
    {
        try
        {
            var intent = new Intent(Intent.ActionGetContent);
            intent.SetType("image/*");
            StartActivityForResult(intent, GalleryRequestCode);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] gallery picker failed: " + ex.Message);
            Toast.MakeText(this, "无法打开相册", ToastLength.Short)?.Show();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != GalleryRequestCode)
        {
            return;
        }

        var uri = data?.Data;
        if (resultCode != Result.Ok || uri is null)
        {
            global::Android.Util.Log.Info(Tag, "[QrScan] scanner gallery canceled");
            return;
        }

        UpdateStatus("正在识别图片…");
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await AndroidQrScanner
                    .DecodeUriAsync(this, uri, CancellationToken.None)
                    .ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        Toast.MakeText(this, "未识别到二维码，请重试", ToastLength.Long)?.Show();
                        UpdateStatus("未识别到二维码，请重试或从相册换一张");
                    }
                    else
                    {
                        Deliver(text);
                    }
                });
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] scanner gallery decode failed: " + ex.Message);
                RunOnUiThread(() => Toast.MakeText(this, "图片读取失败", ToastLength.Long)?.Show());
            }
        });
    }

    private void Deliver(string? text)
    {
        if (_delivered)
        {
            return;
        }

        _delivered = true;
        StopCamera();
        global::Android.Util.Log.Info(
            Tag,
            string.IsNullOrEmpty(text)
                ? "[QrScan] scanner canceled"
                : "[QrScan] scanner result " + AndroidQrScanner.Describe(text));
        var data = new Intent();
        if (!string.IsNullOrEmpty(text))
        {
            data.PutExtra(ExtraResult, text);
        }

        SetResult(string.IsNullOrEmpty(text) ? Result.Canceled : Result.Ok, data);
        Finish();
    }

    // ---- camera callbacks -------------------------------------------------------------------------

    private sealed class PreviewListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly QrScannerActivity _activity;

        public PreviewListener(QrScannerActivity activity) => _activity = activity;

        public void OnSurfaceTextureAvailable(SurfaceTexture? surface, int width, int height)
            => _activity.OnPreviewSurfaceAvailable();

        public bool OnSurfaceTextureDestroyed(SurfaceTexture? surface) => true;

        public void OnSurfaceTextureSizeChanged(SurfaceTexture? surface, int width, int height)
        {
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture? surface)
        {
        }
    }

    private sealed class DeviceCallback : CameraDevice.StateCallback
    {
        private readonly QrScannerActivity _activity;

        public DeviceCallback(QrScannerActivity activity) => _activity = activity;

        public override void OnOpened(CameraDevice camera) => _activity.OnCameraOpened(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            _activity.OnCameraDisconnected();
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            _activity.OnCameraError(error.ToString());
        }
    }

    private sealed class SessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly QrScannerActivity _activity;

        public SessionCallback(QrScannerActivity activity) => _activity = activity;

        public override void OnConfigured(CameraCaptureSession session) => _activity.OnSessionConfigured(session);

        public override void OnConfigureFailed(CameraCaptureSession session)
            => _activity.OnCameraError("capture session configure failed");
    }

    private sealed class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly QrScannerActivity _activity;

        public FrameListener(QrScannerActivity activity) => _activity = activity;

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                _activity.OnFrameAvailable(reader);
            }
        }
    }
}



