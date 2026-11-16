namespace DshRemote.App.Platform;

/// <summary>
/// One encoded long-screenshot page handed to the platform head for persistence. The shared layer owns
/// the pixels; heads only decide where the file lives (Android: MediaStore + app files, desktop: Pictures).
/// </summary>
public sealed record PlatformImagePage(string FileName, byte[] PngBytes, int PixelWidth, int PixelHeight);

/// <summary>
/// A long-screenshot page that reached a user-visible location. <see cref="Location"/> is what the UI
/// shows ("相册/Pictures/DSHRemote/…"), <see cref="LocalPath"/> is a filesystem path when one exists
/// (desktop, Android app-external copy) so the app can preview/share without decoding again.
/// </summary>
public sealed record PlatformSavedImage(
    string FileName,
    string Location,
    string? LocalPath,
    int PixelWidth,
    int PixelHeight);
