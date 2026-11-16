using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Javax.Crypto;
using Javax.Crypto.Spec;
using DshRemote.App.Platform;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// <see cref="ISecretStore"/> backed by an AES-256/GCM key that never leaves the Android Keystore.
/// Only the IV + ciphertext are persisted (in private SharedPreferences); no key material is written to
/// disk, and each entry is bound to its logical key name through GCM additional authenticated data so a
/// ciphertext cannot be replayed under another key.
/// </summary>
public sealed class KeystoreSecretStore : ISecretStore
{
    private const string Tag = "DshRemoteSecrets";
    private const string AndroidKeyStore = "AndroidKeyStore";
    private const string KeyAlias = "dsh_remote_secret_aes_gcm_v1";
    private const string PrefsName = "dsh_remote_secrets";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;
    private const string ValuePrefix = "v1:";

    private readonly object _sync = new();
    private readonly Context _context;

    public KeystoreSecretStore(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(string key) => Task.Run(() => Get(key));

    /// <inheritdoc />
    public Task SetAsync(string key, string value) => Task.Run(() => Set(key, value));

    /// <inheritdoc />
    public Task RemoveAsync(string key) => Task.Run(() => Remove(key));

    /// <summary>Removes every secret written by this store (used by "clear pairing").</summary>
    public Task ClearAsync() => Task.Run(() =>
    {
        lock (_sync)
        {
            try
            {
                var prefs = Prefs;
                var editor = prefs.Edit()
                             ?? throw new InvalidOperationException("SharedPreferences editor unavailable");
                var names = prefs.All?.Keys;
                if (names is not null)
                {
                    foreach (var name in names)
                    {
                        editor.Remove(name);
                    }
                }

                editor.Commit();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "clear failed: " + ex);
            }
        }
    });

    private string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (_sync)
        {
            var storageKey = StorageKey(key);
            string? raw;
            try
            {
                raw = Prefs.GetString(storageKey, null);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "prefs read failed: " + ex);
                return null;
            }

            if (raw is null)
            {
                return null;
            }

            try
            {
                if (!raw.StartsWith(ValuePrefix, StringComparison.Ordinal))
                {
                    throw new FormatException("unrecognised secret encoding");
                }

                var parts = raw.Substring(ValuePrefix.Length).Split(':');
                if (parts.Length != 2)
                {
                    throw new FormatException("expected iv:ciphertext");
                }

                var iv = Convert.FromBase64String(parts[0]);
                var cipherText = Convert.FromBase64String(parts[1]);

                var cipher = CreateCipher();
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(createIfMissing: false), new GCMParameterSpec(GcmTagBits, iv));
                cipher.UpdateAAD(Aad(key));
                var plain = cipher.DoFinal(cipherText)
                            ?? throw new InvalidOperationException("AES/GCM returned no plaintext");
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                // Key was invalidated (factory reset / biometric change) or the blob is corrupt: drop it so
                // the app can re-pair instead of crashing on every read.
                global::Android.Util.Log.Warn(Tag, $"decrypt failed for '{key}', dropping entry: {ex.Message}");
                TryRemove(storageKey);
                return null;
            }
        }
    }

    private void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("secret key must not be empty", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);

        lock (_sync)
        {
            var cipher = CreateCipher();
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey(createIfMissing: true));
            cipher.UpdateAAD(Aad(key));
            var iv = cipher.GetIV() ?? throw new InvalidOperationException("Keystore cipher returned no IV");
            var cipherText = cipher.DoFinal(Encoding.UTF8.GetBytes(value))
                             ?? throw new InvalidOperationException("AES/GCM returned no ciphertext");
            var raw = ValuePrefix + Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(cipherText);

            var editor = Prefs.Edit()
                         ?? throw new InvalidOperationException("SharedPreferences editor unavailable");
            if (!editor.PutString(StorageKey(key), raw)!.Commit())
            {
                throw new IOException("SharedPreferences commit failed for secret '" + key + "'");
            }
        }
    }

    private void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            TryRemove(StorageKey(key));
        }
    }

    private void TryRemove(string storageKey)
    {
        try
        {
            var editor = Prefs.Edit();
            editor?.Remove(storageKey)?.Commit();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "remove failed: " + ex);
        }
    }

    private static Cipher CreateCipher()
        => Cipher.GetInstance(Transformation)
           ?? throw new InvalidOperationException("AES/GCM/NoPadding is unavailable");

    /// <summary>Gets the Keystore AES key, creating it on first use when allowed.</summary>
    private static Java.Security.IKey GetOrCreateKey(bool createIfMissing)
    {
        var keyStore = Java.Security.KeyStore.GetInstance(AndroidKeyStore)
                       ?? throw new InvalidOperationException("AndroidKeyStore unavailable");
        keyStore.Load(null, null);

        if (keyStore.ContainsAlias(KeyAlias))
        {
            return keyStore.GetKey(KeyAlias, null)
                   ?? throw new InvalidOperationException("Keystore alias exists but returned no key");
        }

        if (!createIfMissing)
        {
            throw new KeyNotFoundException("no Keystore key yet (nothing has been stored)");
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, AndroidKeyStore)
                        ?? throw new InvalidOperationException("AES KeyGenerator unavailable");
        var builder = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256);
        generator.Init(builder.Build());
        return generator.GenerateKey()
               ?? throw new InvalidOperationException("KeyGenerator returned no key");
    }

    private ISharedPreferences Prefs =>
        _context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
        ?? throw new InvalidOperationException("SharedPreferences unavailable");

    /// <summary>Hash the logical key name so no secret names are visible in the prefs file.</summary>
    private static string StorageKey(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "secret_" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] Aad(string key) => Encoding.UTF8.GetBytes("dsh-remote-secret:" + key);
}
