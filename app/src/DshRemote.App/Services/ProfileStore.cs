using System.Text.Json;
using System.Text.Json.Serialization;
using DshRemote.App.Platform;

namespace DshRemote.App.Services;

/// <summary>Persisted desktop pairing entry (one saved DSH desktop).</summary>
public sealed class DesktopProfileRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Token { get; set; } = string.Empty;

    public string? SpkiSha256 { get; set; }

    /// <summary>Reserved for v2 pairing metadata (gateway/mode); null on older hosts.</summary>
    public string? Gateway { get; set; }

    public string? Mode { get; set; }

    /// <summary>v2 gateway reachability, when the pairing document reported it.</summary>
    public bool? GatewayOnline { get; set; }

    /// <summary>v1 LAN fallback URI embedded in a v2 pairing document.</summary>
    public string? LocalFallbackUri { get; set; }

    public long LastConnectedAt { get; set; }

    public string? LastStatus { get; set; }
}

public sealed class ProfileStoreDocument
{
    public string? ActiveId { get; set; }

    public List<DesktopProfileRecord> Profiles { get; set; } = new();
}

/// <summary>
/// Multi-desktop profile storage: the platform secret store (Android Keystore) when registered, plus a
/// plain file fallback under <see cref="AppHost.StateDirectory"/> for the desktop shell. The legacy single
/// pairing (<c>dsh.pairing</c> / settings.json) is migrated into the first profile by the view model.
/// </summary>
public static class ProfileStore
{
    public const string SecretKey = "dsh.profiles";
    private const string FileName = "profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<ProfileStoreDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        string? json = null;
        var secretStore = PlatformServices.SecretStore;
        if (secretStore is not null)
        {
            try
            {
                json = await secretStore.GetAsync(SecretKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProfileStore] secret read failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var path = Path.Combine(AppHost.StateDirectory, FileName);
                if (File.Exists(path))
                {
                    json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProfileStore] file read failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileStoreDocument>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] parse failed: {ex.Message}");
            return null;
        }
    }

    public static async Task SaveAsync(ProfileStoreDocument document, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);

        var secretStore = PlatformServices.SecretStore;
        if (secretStore is not null)
        {
            try
            {
                await secretStore.SetAsync(SecretKey, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProfileStore] secret write failed: {ex.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(AppHost.StateDirectory);
            var path = Path.Combine(AppHost.StateDirectory, FileName);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProfileStore] file write failed: {ex.Message}");
        }
    }
}
