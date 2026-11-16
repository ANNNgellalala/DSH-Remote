namespace DshRemote.App.Platform;

/// <summary>
/// Small async key/value store for secrets (pairing token, cookies). The shared UI never depends on a
/// platform SDK: the Android head registers a Keystore-backed implementation into
/// <see cref="PlatformServices.SecretStore"/>; the desktop head leaves it null and the shared layer falls
/// back to its file store.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from any thread. Keys are logical names such as
/// <c>pairing.token</c>; values are UTF-8 strings (may contain <c>:</c>/newlines).
/// </remarks>
public interface ISecretStore
{
    /// <summary>Returns the stored value, or <see langword="null"/> when the key is absent or unreadable.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores (or replaces) the value for <paramref name="key"/>.</summary>
    Task SetAsync(string key, string value);

    /// <summary>Removes <paramref name="key"/> if present. Removing a missing key is a no-op.</summary>
    Task RemoveAsync(string key);
}
