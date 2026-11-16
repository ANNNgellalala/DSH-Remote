using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshRemote.Core;

namespace DshRemote.App.Services;

/// <summary>
/// Local, best-effort cache for the phone UI: session list, workspace baseline, the last rendered window
/// of each session and attachment bytes. The cache is what makes a cold start cheap: instead of pulling
/// ~1 MB of session list and ~1–3 MB of transcript per open, the app paints from disk and asks the desktop
/// only for the delta (the follow baseline is seeded from the cached seq).
/// <para>
/// Everything is best-effort: a missing, corrupt or partially written file is treated as a miss and never
/// surfaces to the user. Documents carry a schema version, and unknown JSON properties are preserved via
/// <see cref="JsonExtensionDataAttribute"/> so a future build can add fields without breaking this one.
/// </para>
/// <para>
/// Layout under <c>&lt;StateDirectory&gt;/cache</c>:
/// <c>sessions.json.gz</c>, <c>workspace.json.gz</c>, <c>drafts.&lt;desktopKey&gt;.json.gz</c>,
/// <c>sessions/&lt;id&gt;.json.gz</c>, <c>images/&lt;sha256&gt;.&lt;ext&gt;</c>.
/// </para>
/// </summary>
public sealed class SessionCacheStore
{
    public const int SchemaVersion = 1;

    /// <summary>Maximum number of event records kept per session (newest window, seq-ordered).</summary>
    public const int MaxCachedRecords = 400;

    private const int MaxCachedSessions = 20;
    private const long MaxSessionCacheBytes = 24L * 1024 * 1024;
    private const long MaxImageCacheBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _writeGate = new();

    public SessionCacheStore(string rootDirectory, string desktopKey)
    {
        RootDirectory = rootDirectory;
        DesktopKey = desktopKey;
    }

    /// <summary>Absolute cache root (e.g. <c>.../DshRemote/cache</c>).</summary>
    public string RootDirectory { get; }

    /// <summary>Stable per-desktop key; scopes all cached data so two desktops never mix.</summary>
    public string DesktopKey { get; }

    /// <summary>
    /// Builds a store for a pairing. Returns <c>null</c> only when no pairing is known (demo mode); a cache
    /// failure while writing is swallowed by the individual save methods.
    /// </summary>
    public static SessionCacheStore? ForPairing(DshPairingInfo? pairing, string? rootOverride = null)
    {
        if (pairing is null)
        {
            return null;
        }

        try
        {
            var root = Path.Combine(
                string.IsNullOrWhiteSpace(rootOverride) ? AppHost.StateDirectory : rootOverride,
                "cache");
            return new SessionCacheStore(root, KeyFor(pairing));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] cannot create store: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Hashes the pairing identity into a filesystem-safe key. The token is hashed, never stored in clear;
    /// including it keeps two desktops behind the same public gateway in separate cache namespaces.
    /// </summary>
    public static string KeyFor(DshPairingInfo? pairing)
    {
        var identity = pairing is null
            ? "anonymous"
            : $"{pairing.Host}:{pairing.Port}|{pairing.SpkiSha256 ?? string.Empty}|{TokenDigest(pairing.Token)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
    }

    private static string TokenDigest(string? token)
        => string.IsNullOrEmpty(token)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16].ToLowerInvariant();

    // ---- session list -------------------------------------------------------------------------------

    /// <summary>Loads the cached compact session list, when one was written for this desktop.</summary>
    public bool TryLoadSessionList(out CachedSessionList? list)
    {
        list = ReadGzipJson<CachedSessionList>(Path.Combine(RootDirectory, "sessions.json.gz"));
        if (list is null || list.Version <= 0 || list.Version > SchemaVersion ||
            !string.Equals(list.DesktopKey, DesktopKey, StringComparison.Ordinal) ||
            list.Items.Count == 0)
        {
            list = null;
            return false;
        }

        list.Items.RemoveAll(item => string.IsNullOrWhiteSpace(item.SessionId));
        return list.Items.Count > 0;
    }

    /// <summary>Persists the fresh compact list; called after every successful list refresh.</summary>
    public void SaveSessionList(
        IReadOnlyList<SessionSummary> items,
        string? cursor,
        int totalCount,
        int hiddenSubagentCount,
        bool subagentsFiltered)
    {
        try
        {
            var document = new CachedSessionList
            {
                Version = SchemaVersion,
                DesktopKey = DesktopKey,
                SavedAt = DateTimeOffset.Now,
                Cursor = cursor,
                TotalCount = totalCount > 0 ? totalCount : items.Count,
                HiddenSubagentCount = Math.Max(0, hiddenSubagentCount),
                SubagentsFiltered = subagentsFiltered,
                Items = items.Select(CachedSessionItem.FromSummary).ToList()
            };
            WriteGzipJson(Path.Combine(RootDirectory, "sessions.json.gz"), document);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] save session list failed: {ex.Message}");
        }
    }

    // ---- workspace ----------------------------------------------------------------------------------

    public bool TryLoadWorkspace(out DshWorkspaceState? state)
    {
        var document = ReadGzipJson<CachedWorkspaceFile>(Path.Combine(RootDirectory, "workspace.json.gz"));
        if (document is null || document.Version <= 0 || document.Version > SchemaVersion ||
            !string.Equals(document.DesktopKey, DesktopKey, StringComparison.Ordinal))
        {
            state = null;
            return false;
        }

        state = document.ToState();
        return true;
    }

    public void SaveWorkspace(DshWorkspaceState? state)
    {
        if (state is null)
        {
            return;
        }

        try
        {
            var document = CachedWorkspaceFile.FromState(state);
            document.DesktopKey = DesktopKey;
            document.Version = SchemaVersion;
            document.SavedAt = DateTimeOffset.Now;
            WriteGzipJson(Path.Combine(RootDirectory, "workspace.json.gz"), document);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] save workspace failed: {ex.Message}");
        }
    }

    // ---- composer drafts -----------------------------------------------------------------------------

    /// <summary>
    /// Drafts are namespaced by desktop key: two saved desktops share the cache root, and a single
    /// <c>drafts.json.gz</c> let the last flush overwrite (or delete) the other desktop's drafts.
    /// </summary>
    private string DraftsPath => Path.Combine(RootDirectory, $"drafts.{DesktopKey}.json.gz");

    /// <summary>
    /// Loads the persisted per-session composer drafts. Missing/corrupt documents are a miss, never an
    /// error: drafts are a convenience, not session data.
    /// </summary>
    public bool TryLoadDrafts(out Dictionary<string, string> drafts)
    {
        drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = ReadGzipJson<CachedDraftsFile>(DraftsPath);
        if (document is null || document.Version <= 0 || document.Version > SchemaVersion ||
            !string.Equals(document.DesktopKey, DesktopKey, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (sessionId, text) in document.Drafts)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(text))
            {
                drafts[sessionId] = text;
            }
        }

        return drafts.Count > 0;
    }

    /// <summary>Persists the per-session composer drafts; an empty map removes this desktop's document.</summary>
    public void SaveDrafts(IReadOnlyDictionary<string, string> drafts)
    {
        try
        {
            var path = DraftsPath;
            if (drafts.Count == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            var document = new CachedDraftsFile
            {
                Version = SchemaVersion,
                DesktopKey = DesktopKey,
                SavedAt = DateTimeOffset.Now,
                Drafts = drafts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
            WriteGzipJson(path, document);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] save drafts failed: {ex.Message}");
        }
    }

    // ---- per-session window -----------------------------------------------------------------------

    public bool TryLoadMessages(string sessionId, out CachedSessionMessages? messages)
    {
        messages = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var document = ReadGzipJson<CachedSessionMessages>(MessagePath(sessionId));
        if (document is null || document.Version <= 0 || document.Version > SchemaVersion ||
            !string.Equals(document.SessionId, sessionId, StringComparison.Ordinal) ||
            document.LastSeq <= 0 || document.Events.Count == 0)
        {
            return false;
        }

        messages = document;
        return true;
    }

    /// <summary>Persists the rendered window. Records are de-duplicated by seq, sorted and capped.</summary>
    public void SaveMessages(
        string sessionId,
        long lastSeq,
        long cursor,
        IReadOnlyList<SessionEventRecord> records)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || records.Count == 0 || lastSeq <= 0)
        {
            return;
        }

        try
        {
            var bySeq = new SortedDictionary<long, CachedEvent>();
            foreach (var record in records)
            {
                if (record.Seq <= 0)
                {
                    continue;
                }

                bySeq[record.Seq] = CachedEvent.FromRecord(record);
            }

            if (bySeq.Count > MaxCachedRecords)
            {
                foreach (var seq in bySeq.Keys.Take(bySeq.Count - MaxCachedRecords).ToList())
                {
                    bySeq.Remove(seq);
                }
            }

            var document = new CachedSessionMessages
            {
                Version = SchemaVersion,
                SessionId = sessionId,
                SavedAt = DateTimeOffset.Now,
                LastSeq = lastSeq,
                Cursor = Math.Max(cursor, lastSeq),
                OldestSeq = bySeq.Count > 0 ? bySeq.Keys.First() : 0,
                Events = bySeq.Values.ToList()
            };
            WriteGzipJson(MessagePath(sessionId), document);
            PruneSessionFiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] save messages for {sessionId} failed: {ex.Message}");
        }
    }

    public void DeleteMessages(string sessionId)
    {
        try
        {
            var path = MessagePath(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] delete messages failed: {ex.Message}");
        }
    }

    // ---- images -------------------------------------------------------------------------------------

    /// <summary>Reads cached attachment bytes by attachment id.</summary>
    public bool TryReadImage(string attachmentId, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            foreach (var path in ImagePaths(attachmentId))
            {
                if (File.Exists(path))
                {
                    bytes = File.ReadAllBytes(path);
                    return bytes.Length > 0;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] read image failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>Stores attachment bytes content-addressed by attachment id (sha256), pruned by size.</summary>
    public void SaveImage(string attachmentId, byte[] bytes, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || bytes.Length == 0)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(RootDirectory, "images");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ImageFileName(attachmentId, mediaType));
            if (File.Exists(path))
            {
                return;
            }

            WriteAtomic(path, bytes);
            PruneImageFiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] save image failed: {ex.Message}");
        }
    }

    // ---- maintenance --------------------------------------------------------------------------------

    /// <summary>Drops the oldest session/image files beyond the configured budgets.</summary>
    public void Prune()
    {
        PruneSessionFiles();
        PruneImageFiles();
    }

    private void PruneSessionFiles()
    {
        try
        {
            var directory = Path.Combine(RootDirectory, "sessions");
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = new DirectoryInfo(directory).GetFiles("*.json.gz")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            long total = 0;
            for (var index = 0; index < files.Count; index++)
            {
                total += files[index].Length;
                if (index < MaxCachedSessions && total <= MaxSessionCacheBytes)
                {
                    continue;
                }

                try
                {
                    files[index].Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[cache] prune session file failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] prune sessions failed: {ex.Message}");
        }
    }

    private void PruneImageFiles()
    {
        try
        {
            var directory = Path.Combine(RootDirectory, "images");
            if (!Directory.Exists(directory))
            {
                return;
            }

            long total = 0;
            foreach (var file in new DirectoryInfo(directory).GetFiles().OrderByDescending(file => file.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total <= MaxImageCacheBytes)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[cache] prune image failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] prune images failed: {ex.Message}");
        }
    }

    // ---- file helpers -------------------------------------------------------------------------------

    private string MessagePath(string sessionId)
    {
        var safe = new StringBuilder(sessionId.Length);
        foreach (var ch in sessionId)
        {
            safe.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return Path.Combine(RootDirectory, "sessions", $"{safe}.json.gz");
    }

    private string[] ImagePaths(string attachmentId)
    {
        var baseName = ImageBaseName(attachmentId);
        var directory = Path.Combine(RootDirectory, "images");
        var candidates = new List<string>();
        try
        {
            if (Directory.Exists(directory))
            {
                candidates.AddRange(Directory.GetFiles(directory, $"{baseName}.*"));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] list images failed: {ex.Message}");
        }

        candidates.Add(Path.Combine(directory, baseName));
        return candidates.ToArray();
    }

    private static string ImageBaseName(string attachmentId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attachmentId))).ToLowerInvariant();

    private static string ImageFileName(string attachmentId, string? mediaType)
    {
        var hash = ImageBaseName(attachmentId);
        var extension = ExtensionFor(mediaType);
        return string.IsNullOrEmpty(extension) ? hash : $"{hash}.{extension}";
    }

    private static string ExtensionFor(string? mediaType)
    {
        var type = mediaType ?? string.Empty;
        if (type.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return "png";
        }

        if (type.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || type.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            return "jpg";
        }

        if (type.Contains("webp", StringComparison.OrdinalIgnoreCase))
        {
            return "webp";
        }

        if (type.Contains("gif", StringComparison.OrdinalIgnoreCase))
        {
            return "gif";
        }

        return "bin";
    }

    private static T? ReadGzipJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(gzip, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cache] read {Path.GetFileName(path)} failed: {ex.Message}");
            return null;
        }
    }

    private void WriteGzipJson<T>(string path, T value)
    {
        lock (_writeGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootDirectory);
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                JsonSerializer.Serialize(gzip, value, JsonOptions);
            }

            WriteAtomic(path, buffer.ToArray());
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}

/// <summary>Serialized compact session list (schema 1). Unknown fields survive via <see cref="Extra"/>.</summary>
public sealed class CachedSessionList
{
    public int Version { get; set; }

    public string DesktopKey { get; set; } = string.Empty;

    public DateTimeOffset SavedAt { get; set; }

    public string? Cursor { get; set; }

    public int TotalCount { get; set; }

    public int HiddenSubagentCount { get; set; }

    /// <summary>True when the server already excluded subagent rows (compact list endpoint).</summary>
    public bool SubagentsFiltered { get; set; }

    public List<CachedSessionItem> Items { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One cached session summary; only fields the list UI needs plus a passthrough bag.</summary>
public sealed class CachedSessionItem
{
    public string SessionId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Cwd { get; set; }

    public string? Model { get; set; }

    public bool Running { get; set; }

    public bool AgentAvailable { get; set; } = true;

    public bool Blank { get; set; }

    public long UpdatedAt { get; set; }

    public long LastPromptAt { get; set; }

    public string? ParentSessionId { get; set; }

    public string? Origin { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static CachedSessionItem FromSummary(SessionSummary summary) => new()
    {
        SessionId = summary.SessionId,
        Title = summary.Title,
        Cwd = summary.Cwd,
        Model = summary.Model,
        Running = summary.Running,
        AgentAvailable = summary.AgentAvailable,
        Blank = summary.Blank,
        UpdatedAt = summary.UpdatedAt,
        LastPromptAt = summary.LastPromptAt,
        ParentSessionId = summary.ParentSessionId,
        Origin = summary.Origin
    };

    public SessionSummary ToSummary() => new(
        SessionId,
        Title,
        Cwd,
        Model,
        Running,
        AgentAvailable,
        Blank,
        UpdatedAt,
        LastPromptAt,
        ParentSessionId,
        Origin);
}

/// <summary>Serialized workspace baseline used to render the grouped list before the desktop answers.</summary>
public sealed class CachedWorkspaceFile
{
    public int Version { get; set; }

    public string DesktopKey { get; set; } = string.Empty;

    public DateTimeOffset SavedAt { get; set; }

    public List<CachedWorkspace> Workspaces { get; set; } = new();

    public List<string> ArchivedSessionIds { get; set; } = new();

    public List<string> PinnedSessionIds { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static CachedWorkspaceFile FromState(DshWorkspaceState state) => new()
    {
        Workspaces = state.Workspaces.Select(workspace => new CachedWorkspace
        {
            WorkspaceId = workspace.WorkspaceId,
            Path = workspace.Path,
            Title = workspace.Title,
            SessionIds = workspace.SessionIds.ToList(),
            CreatedAt = workspace.CreatedAt,
            UpdatedAt = workspace.UpdatedAt
        }).ToList(),
        ArchivedSessionIds = state.ArchivedSessionIds.ToList(),
        PinnedSessionIds = state.PinnedSessionIds.ToList()
    };

    public DshWorkspaceState ToState() => new(
        Workspaces.Select(workspace => new DshWorkspace(
            workspace.WorkspaceId,
            workspace.Path,
            workspace.Title,
            workspace.SessionIds,
            workspace.CreatedAt,
            workspace.UpdatedAt)).ToList(),
        ArchivedSessionIds,
        PinnedSessionIds);
}

public sealed class CachedWorkspace
{
    public string WorkspaceId { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<string> SessionIds { get; set; } = new();

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Serialized per-session composer drafts (schema 1). Only sessions with a non-blank draft are stored;
/// the count and per-draft length caps are enforced by <see cref="SessionDraftStore"/> before saving.
/// </summary>
public sealed class CachedDraftsFile
{
    public int Version { get; set; }

    public string DesktopKey { get; set; } = string.Empty;

    public DateTimeOffset SavedAt { get; set; }

    public Dictionary<string, string> Drafts { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Serialized transcript window plus the seq/cursor reconciliation state for incremental sync.</summary>
public sealed class CachedSessionMessages
{
    public int Version { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public DateTimeOffset SavedAt { get; set; }

    /// <summary>Highest durable seq rendered when the window was saved (follow baseline seed).</summary>
    public long LastSeq { get; set; }

    /// <summary>Server log cut observed at save time (feeding the follow repair read).</summary>
    public long Cursor { get; set; }

    public long OldestSeq { get; set; }

    public List<CachedEvent> Events { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Rebuilds the durable event records the transcript was rendered from.</summary>
    public IReadOnlyList<SessionEventRecord> ToRecords()
    {
        var records = new List<SessionEventRecord>(Events.Count);
        foreach (var cached in Events)
        {
            if (cached.Seq <= 0)
            {
                continue;
            }

            records.Add(cached.ToRecord());
        }

        return records;
    }
}

/// <summary>One cached event: the payload plus every unknown top-level field (extension space).</summary>
public sealed class CachedEvent
{
    public long Seq { get; set; }

    public long Time { get; set; }

    public string Type { get; set; } = string.Empty;

    public JsonElement Data { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static CachedEvent FromRecord(SessionEventRecord record)
    {
        CachedEvent cached;
        try
        {
            using var document = JsonDocument.Parse(record.RawJson);
            cached = new CachedEvent
            {
                Seq = record.Seq,
                Time = record.Time,
                Type = record.Type,
                Data = NormalizeData(record.Data)
            };
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is "seq" or "time" or "type" or "data")
                {
                    continue;
                }

                cached.Extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                cached.Extra[property.Name] = property.Value.Clone();
            }
        }
        catch
        {
            cached = new CachedEvent
            {
                Seq = record.Seq,
                Time = record.Time,
                Type = record.Type,
                Data = NormalizeData(record.Data)
            };
        }

        return cached;
    }

    /// <summary>
    /// A default <see cref="JsonElement"/> cannot be serialized; normalize it to an explicit JSON null so a
    /// record without a payload still round-trips instead of failing the whole cache write.
    /// </summary>
    private static JsonElement NormalizeData(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Undefined)
        {
            return data.Clone();
        }

        using var nullDocument = JsonDocument.Parse("null");
        return nullDocument.RootElement.Clone();
    }

    public SessionEventRecord ToRecord()
    {
        var raw = BuildRawJson();
        // The cached envelope keeps unknown top-level fields (surfaceOp/sourceEventSeqs/ignorable) in Extra,
        // so re-run the same parser the wire uses instead of hand-copying only the five base fields.
        if (SessionEventRecord.FromRawJson(raw) is { } parsed)
        {
            return parsed;
        }

        JsonElement data = default;
        if (Data.ValueKind != JsonValueKind.Undefined && Data.ValueKind != JsonValueKind.Null)
        {
            data = Data.Clone();
        }

        return new SessionEventRecord(Seq, Time, Type, data, raw);
    }

    private string BuildRawJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", Seq);
            writer.WriteNumber("time", Time);
            writer.WriteString("type", Type);
            writer.WritePropertyName("data");
            if (Data.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
            }
            else
            {
                Data.WriteTo(writer);
            }

            if (Extra is not null)
            {
                foreach (var (name, value) in Extra)
                {
                    if (name is "seq" or "time" or "type" or "data")
                    {
                        continue;
                    }

                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
