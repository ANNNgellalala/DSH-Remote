using System.Diagnostics;

namespace DshRemote.App.Services;

/// <summary>
/// Remembers the unsent composer text of every session.
/// <para>
/// Opening a conversation builds a fresh <see cref="ViewModels.SessionViewModel"/>, so without this store
/// a draft died the moment the user switched chats or went back to the session list — most visibly in a
/// workspace's newly created (blank) session, where the user switches away to check something and comes
/// back to an empty composer. Entries live in memory and are best-effort persisted through
/// <see cref="SessionCacheStore"/> (one gzip document per desktop, bounded by count and length) so a
/// cold start restores them too.
/// </para>
/// <para>Every method is thread-safe and never throws: a cache failure only loses persistence, never text.</para>
/// </summary>
public sealed class SessionDraftStore
{
    /// <summary>Longest draft persisted to disk; longer drafts stay in memory only.</summary>
    public const int MaxPersistedDraftLength = 4000;

    /// <summary>Newest drafts persisted to disk; older entries are dropped when the cap is exceeded.</summary>
    public const int MaxPersistedDrafts = 100;

    private readonly Dictionary<string, Entry> _drafts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private SessionCacheStore? _cache;
    private string? _desktopKey;
    private long _sequence;
    private bool _dirty;

    private sealed class Entry
    {
        public string Text { get; init; } = string.Empty;

        /// <summary>Monotonic touch order; higher is newer (used to cap what reaches the disk).</summary>
        public long Touched { get; init; }
    }

    /// <summary>Number of sessions currently holding a non-empty draft.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _drafts.Count;
            }
        }
    }

    /// <summary>
    /// Binds the store to the current connection's cache. Re-attaching the same desktop keeps the live
    /// in-memory text (it may be newer than the file); switching to a different desktop replaces the set
    /// with that desktop's persisted drafts so session ids from two hosts never mix.
    /// </summary>
    public void Attach(SessionCacheStore? cache)
    {
        if (cache is null)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_desktopKey, cache.DesktopKey, StringComparison.Ordinal))
            {
                _cache = cache;
                return;
            }

            // Switching desktops replaces the in-memory set; flush the outgoing desktop's drafts to its
            // own cache document first so connecting elsewhere cannot drop them.
            Flush();

            _cache = cache;
            _desktopKey = cache.DesktopKey;
            _drafts.Clear();
            _sequence = 0;
            _dirty = false;

            try
            {
                if (cache.TryLoadDrafts(out var persisted))
                {
                    foreach (var (sessionId, text) in persisted)
                    {
                        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        _drafts[sessionId] = new Entry { Text = text, Touched = ++_sequence };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[drafts] load failed: {ex.Message}");
            }
        }
    }

    /// <summary>Returns the stored draft, or an empty string when the session has none.</summary>
    public string Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return string.Empty;
        }

        lock (_gate)
        {
            return _drafts.TryGetValue(sessionId!, out var entry) ? entry.Text : string.Empty;
        }
    }

    /// <summary>Stores (or, for blank text, clears) one session's draft and marks it for persistence.</summary>
    public void Set(string? sessionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_drafts.Remove(sessionId!))
                {
                    _dirty = true;
                }

                return;
            }

            _drafts[sessionId!] = new Entry { Text = text!, Touched = ++_sequence };
            _dirty = true;
        }
    }

    /// <summary>Drops one session's draft (e.g. after it was sent).</summary>
    public void Clear(string? sessionId) => Set(sessionId, null);

    /// <summary>
    /// Writes dirty drafts to the cache. No-op when clean or when no cache is attached; the write is
    /// best-effort and never surfaces an error to the caller.
    /// </summary>
    public void Flush()
    {
        SessionCacheStore? cache;
        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            if (!_dirty || _cache is null)
            {
                return;
            }

            cache = _cache;
            snapshot = _drafts
                .Where(pair => pair.Value.Text.Length <= MaxPersistedDraftLength)
                .OrderByDescending(pair => pair.Value.Touched)
                .Take(MaxPersistedDrafts)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal);
            _dirty = false;
        }

        cache.SaveDrafts(snapshot);
    }
}
