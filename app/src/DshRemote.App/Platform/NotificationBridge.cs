using System.Diagnostics;

namespace DshRemote.App.Platform;

/// <summary>
/// Platform implementation of local notifications. Registered by a head (Android) through
/// <see cref="NotificationBridge.Implementation"/>; when no head registers one every call is a no-op, which
/// is what the desktop head relies on.
/// </summary>
public interface INotificationBridge
{
    /// <summary>An approval is waiting for the user (tapping should open <paramref name="sessionId"/>).</summary>
    void NotifyApprovalNeeded(string title, string body, string? sessionId);

    /// <summary>The agent asked the user a question.</summary>
    void NotifyQuestionNeeded(string title, string body, string? sessionId);

    /// <summary>A turn finished while the app was in the background.</summary>
    void NotifyTurnFinished(string title, string body);
}

/// <summary>
/// Shared notification facade. The shared UI calls these static methods; the Android head supplies the
/// implementation, and failures never propagate into the UI (a missing notification must not break a
/// connection or a session).
/// </summary>
public static class NotificationBridge
{
    /// <summary>Platform implementation; null on platforms without local notifications (desktop).</summary>
    public static INotificationBridge? Implementation { get; set; }

    /// <summary>True when notifications are wired for this platform.</summary>
    public static bool IsAvailable => Implementation is not null;

    public static void NotifyApprovalNeeded(string title, string body, string? sessionId = null)
    {
        Safe(bridge => bridge.NotifyApprovalNeeded(title ?? string.Empty, body ?? string.Empty, sessionId));
    }

    public static void NotifyQuestionNeeded(string title, string body, string? sessionId = null)
    {
        Safe(bridge => bridge.NotifyQuestionNeeded(title ?? string.Empty, body ?? string.Empty, sessionId));
    }

    public static void NotifyTurnFinished(string title, string body)
    {
        Safe(bridge => bridge.NotifyTurnFinished(title ?? string.Empty, body ?? string.Empty));
    }

    private static void Safe(Action<INotificationBridge> action)
    {
        var bridge = Implementation;
        if (bridge is null)
        {
            return;
        }

        try
        {
            action(bridge);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationBridge] notification failed: {ex}");
        }
    }
}
