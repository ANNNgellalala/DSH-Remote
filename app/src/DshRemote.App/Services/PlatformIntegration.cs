using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Threading;
using DshRemote.App.Platform;
using DshRemote.App.ViewModels;

namespace DshRemote.App.Services;

/// <summary>
/// Shared-layer bridge to the optional platform services the heads register in
/// <see cref="PlatformServices"/> / <see cref="NotificationBridge"/>.
/// <para>
/// Everything here is optional: the desktop head registers nothing and the app still works. Newer Android
/// heads may add fields the shared app does not compile against yet (foreground flag, safe-area insets, IME
/// height, back-press handler); those are read by reflection so the shared UI can light up without
/// recompiling against a specific head build.
/// </para>
/// </summary>
public static class PlatformIntegration
{
    private const string PairingSecretKey = "dsh.pairing";

    private static readonly Type ServicesType = typeof(PlatformServices);

    /// <summary>True when the platform reports the app is in the foreground. Defaults to true (desktop).</summary>
    public static bool IsAppForeground
    {
        get
        {
            var value = ReadMember("AppForeground") ?? ReadMember("IsAppForeground") ?? ReadMember("IsForeground");
            return value switch
            {
                bool flag => flag,
                _ => !PlatformServices.IsAndroid // unknown on Android: assume background so notifications are not lost
            };
        }
    }

    /// <summary>Safe-area padding reported by the platform (zero when the head does not expose it).</summary>
    public static Thickness SafeAreaPadding
    {
        get
        {
            var value = ReadMember("SafeAreaInsets") ?? ReadMember("SafeAreaPadding");
            return value switch
            {
                Thickness thickness => thickness,
                null => default,
                _ => TryReadThickness(value)
            };
        }
    }

    /// <summary>Soft-keyboard height reported by the platform (zero when the head does not expose it).</summary>
    public static double ImeHeight
    {
        get
        {
            var value = ReadMember("ImeHeight") ?? ReadMember("KeyboardHeight");
            return value switch
            {
                double number => number,
                int number => number,
                float number => number,
                _ => 0
            };
        }
    }

    /// <summary>Wires deep links / notification taps / back handling and syncs the foreground service state.</summary>
    public static void Wire(MainViewModel viewModel)
    {
        try
        {
            PlatformServices.OnDeepLink = (uri, source) =>
            {
                Debug.WriteLine($"[PlatformIntegration] deep link ({source}): {PlatformServices.Redact(uri)}");
                Dispatcher.UIThread.Post(() => _ = viewModel.ConnectFromDeepLinkAsync(uri));
            };

            PlatformServices.OnSessionOpenRequested = sessionId =>
                Dispatcher.UIThread.Post(() => _ = viewModel.OpenSessionByIdAsync(sessionId));

            WireBackHandler(viewModel.HandleBackRequest);
            PlatformServices.SetConnectionActive(viewModel.IsConnected);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] wire failed: {ex}");
        }
    }

    public static void SetConnectionActive(bool active)
    {
        try
        {
            PlatformServices.SetConnectionActive(active);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] foreground connection state failed: {ex}");
        }
    }

    /// <summary>Persists the pairing document in the platform secret store when one is registered.</summary>
    public static async Task PersistPairingAsync(string? json)
    {
        var store = PlatformServices.SecretStore;
        if (store is null || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            await store.SetAsync(PairingSecretKey, json!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] secret store write failed: {ex.Message}");
        }
    }

    public static async Task<string?> ReadPairingAsync()
    {
        var store = PlatformServices.SecretStore;
        if (store is null)
        {
            return null;
        }

        try
        {
            return await store.GetAsync(PairingSecretKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] secret store read failed: {ex.Message}");
            return null;
        }
    }

    public static async Task ClearPairingAsync()
    {
        var store = PlatformServices.SecretStore;
        if (store is null)
        {
            return;
        }

        try
        {
            await store.RemoveAsync(PairingSecretKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] secret store clear failed: {ex.Message}");
        }
    }

    // ---- Notifications (only when the app is not visibly showing that session) --------------------

    public static void NotifyApprovalNeeded(string toolName, string body, string? sessionId, bool sessionVisible)
    {
        if (IsAppForeground && sessionVisible)
        {
            return;
        }

        NotificationBridge.NotifyApprovalNeeded("需要审批: " + toolName, body, sessionId);
    }

    public static void NotifyQuestionNeeded(string body, string? sessionId, bool sessionVisible)
    {
        if (IsAppForeground && sessionVisible)
        {
            return;
        }

        NotificationBridge.NotifyQuestionNeeded("需要你的回答", body, sessionId);
    }

    public static void NotifyTurnFinished(string sessionTitle, string body, bool sessionVisible)
    {
        if (IsAppForeground && sessionVisible)
        {
            return;
        }

        NotificationBridge.NotifyTurnFinished(string.IsNullOrWhiteSpace(sessionTitle) ? "本轮已结束" : sessionTitle, body);
    }

    // ---- Optional head extensions ----------------------------------------------------------------

    private static object? ReadMember(string name)
    {
        try
        {
            var property = ServicesType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property is not null)
            {
                return property.GetValue(null);
            }

            var field = ServicesType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static void WireBackHandler(Func<bool> handler)
    {
        try
        {
            var property = ServicesType.GetProperty("OnBackPressed", BindingFlags.Public | BindingFlags.Static);
            if (property is not null && property.PropertyType.IsAssignableFrom(typeof(Func<bool>)))
            {
                property.SetValue(null, handler);
                return;
            }

            var field = ServicesType.GetField("OnBackPressed", BindingFlags.Public | BindingFlags.Static);
            if (field is not null && field.FieldType.IsAssignableFrom(typeof(Func<bool>)))
            {
                field.SetValue(null, handler);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlatformIntegration] back handler wire failed: {ex.Message}");
        }
    }

    private static Thickness TryReadThickness(object value)
    {
        try
        {
            var type = value.GetType();
            double Read(string name)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(value) is { } raw)
                {
                    return Convert.ToDouble(raw);
                }

                return 0;
            }

            return new Thickness(Read("Left"), Read("Top"), Read("Right"), Read("Bottom"));
        }
        catch
        {
            return default;
        }
    }
}
