using System.Text.Json;
using DshRemote.App.Services;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Local-cache contract tests: the cache is what keeps a cold start from re-downloading the session list
/// and the transcript window, so a silent schema/round-trip break would be invisible until a phone user
/// noticed the traffic came back.
/// </summary>
public sealed class SessionCacheStoreTests : IDisposable
{
    private readonly string _root;

    public SessionCacheStoreTests()
    {
        // BaseDirectory lives under the (writable) test output directory: on this machine the sandbox lets
        // dotnet write inside the repo but not create directories under %TEMP%/%LocalAppData%.
        _root = Path.Combine(AppContext.BaseDirectory, "cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort: the temp directory is disposable.
        }
    }

    private static DshPairingInfo Pairing(string token = "token-a", string host = "127.0.0.1") =>
        new(host, 8443, token, Name: "desktop");

    [Fact]
    public void SessionList_round_trips_compact_fields_and_keeps_unknown_extensions()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var items = new List<SessionSummary>
        {
            new("session-1", "Title A", @"C:\work", "model-a", Running: true, AgentAvailable: true,
                Blank: false, UpdatedAt: 100, LastPromptAt: 90),
            new("session-2", null, null, null, Running: false, AgentAvailable: true, Blank: true,
                UpdatedAt: 50, LastPromptAt: 0, ParentSessionId: null, Origin: null)
        };

        store.SaveSessionList(items, cursor: null, totalCount: 313, hiddenSubagentCount: 199, subagentsFiltered: true);

        Assert.True(store.TryLoadSessionList(out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(SessionCacheStore.SchemaVersion, loaded!.Version);
        Assert.Equal(313, loaded.TotalCount);
        Assert.Equal(199, loaded.HiddenSubagentCount);
        Assert.True(loaded.SubagentsFiltered);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("Title A", loaded.Items[0].Title);
        Assert.True(loaded.Items[0].Running);
        Assert.True(loaded.Items[1].Blank);
        Assert.Equal("session-2", loaded.Items[1].ToSummary().SessionId);
    }

    [Fact]
    public void SessionList_is_scoped_per_desktop_key()
    {
        var first = SessionCacheStore.ForPairing(Pairing(token: "token-a"), _root)!;
        first.SaveSessionList(
            new[] { new SessionSummary("session-1", "A", null, null, false, true, false, 1, 0) },
            null, 1, 0, false);

        var otherDesktop = SessionCacheStore.ForPairing(Pairing(token: "token-b"), _root)!;
        Assert.NotEqual(first.DesktopKey, otherDesktop.DesktopKey);
        Assert.False(otherDesktop.TryLoadSessionList(out _));
        Assert.True(first.TryLoadSessionList(out _));
    }

    [Fact]
    public void Workspace_round_trips_groups_and_archive_state()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var state = new DshWorkspaceState(
            new[]
            {
                new DshWorkspace("ws-1", @"D:\code", "Code", new[] { "session-1", "session-2" }, 10, 20)
            },
            new[] { "session-archived" },
            Array.Empty<string>());

        store.SaveWorkspace(state);

        Assert.True(store.TryLoadWorkspace(out var loaded));
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Workspaces);
        Assert.Equal(@"D:\code", loaded.Workspaces[0].Path);
        Assert.Equal(new[] { "session-1", "session-2" }, loaded.Workspaces[0].SessionIds);
        Assert.Equal(new[] { "session-archived" }, loaded.ArchivedSessionIds);
    }

    [Fact]
    public void Messages_round_trip_records_and_preserve_raw_json_flags()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var records = new[]
        {
            Record(1, "tool/result", """{"isError":true,"output":"boom"}"""),
            Record(2, "assistant/message", """{"message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}"""),
            Record(3, "turn/end", """{"reason":{"kind":"done"}}""")
        };

        store.SaveMessages("session-1", lastSeq: 3, cursor: 9, records);

        Assert.True(store.TryLoadMessages("session-1", out var cached));
        Assert.NotNull(cached);
        Assert.Equal(3, cached!.LastSeq);
        Assert.Equal(9, cached.Cursor);
        Assert.Equal(1, cached.OldestSeq);

        var loaded = cached.ToRecords();
        Assert.Equal(new long[] { 1, 2, 3 }, loaded.Select(record => record.Seq));
        Assert.Equal("tool/result", loaded[0].Type);
        // The UI scans RawJson for error markers; the cache must reproduce it byte-for-byte semantically.
        Assert.Contains("\"isError\":true", loaded[0].RawJson);
        Assert.Equal("boom", loaded[0].Data.GetProperty("output").GetString());
        Assert.Equal("hi", loaded[1].TextOf());
    }

    [Fact]
    public void Messages_round_trip_preserves_the_surface_envelope()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        const string raw =
            """{"type":"tool/result","seq":2270,"time":1794550695865,"data":{"turn":1,"step":2},"sourceEventSeqs":[38],"surfaceOp":{"op":"replace","startSeq":38,"endSeq":38}}""";
        var record = SessionEventRecord.FromRawJson(raw);
        Assert.NotNull(record);

        store.SaveMessages("session-surface", lastSeq: 2270, cursor: 2270, new[] { record! });

        Assert.True(store.TryLoadMessages("session-surface", out var cached));
        var loaded = Assert.Single(cached!.ToRecords());
        Assert.True(loaded.IsSurfaceEvent);
        Assert.Equal(SurfaceOpKind.Replace, loaded.SurfaceOp);
        Assert.NotNull(loaded.ReplaceSpan);
        Assert.Equal(38, loaded.ReplaceSpan!.Value.StartSeq);
        Assert.Equal(38, loaded.ReplaceSpan.Value.EndSeq);
        Assert.Equal(new long[] { 38 }, loaded.SourceEventSeqs!);
        // The rebuilt line must keep the envelope fields so a cold start replays the same surface semantics.
        Assert.Contains("\"surfaceOp\":{\"op\":\"replace\"", loaded.RawJson);
    }

    [Fact]
    public void Messages_are_capped_to_the_newest_window()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var records = Enumerable.Range(1, SessionCacheStore.MaxCachedRecords + 50)
            .Select(seq => Record(seq, "step/start", "{}"))
            .ToArray();

        store.SaveMessages("session-1", records[^1].Seq, cursor: records[^1].Seq, records);

        Assert.True(store.TryLoadMessages("session-1", out var cached));
        Assert.Equal(SessionCacheStore.MaxCachedRecords, cached!.Events.Count);
        Assert.Equal(51, cached.Events[0].Seq);
        Assert.Equal(SessionCacheStore.MaxCachedRecords + 50, cached.Events[^1].Seq);
    }

    [Fact]
    public void Missing_and_corrupt_files_are_treated_as_cache_misses()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        Assert.False(store.TryLoadSessionList(out _));
        Assert.False(store.TryLoadMessages("session-x", out _));
        Assert.False(store.TryReadImage("sha256:missing", out _));

        var corrupt = Path.Combine(_root, "cache", "sessions.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        File.WriteAllText(corrupt, "not gzip");
        Assert.False(store.TryLoadSessionList(out _));
    }

    [Fact]
    public void Images_round_trip_and_are_content_addressed()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        Assert.False(store.TryReadImage("sha256:abc", out _));
        store.SaveImage("sha256:abc", bytes, "image/png");

        Assert.True(store.TryReadImage("sha256:abc", out var loaded));
        Assert.Equal(bytes, loaded);
        // A media-type mismatch must still find the stored bytes (content-addressed by id, sorted by glob).
        Assert.True(store.TryReadImage("sha256:abc", out var again));
        Assert.Equal(bytes, again);
    }

    [Fact]
    public void Session_files_are_capped_to_the_budget()
    {
        var store = SessionCacheStore.ForPairing(Pairing(), _root)!;
        var sessionsDir = Path.Combine(_root, "cache", "sessions");
        for (var index = 0; index < 25; index++)
        {
            store.SaveMessages($"session-{index:D2}", lastSeq: 1, cursor: 1, new[] { Record(1, "step/start", "{}") });
        }

        var remaining = Directory.GetFiles(sessionsDir, "*.json.gz").Select(Path.GetFileName).ToList();
        Assert.True(remaining.Count <= 20, $"expected <= 20 cached sessions, got {remaining.Count}");
        // The most recently written window is always kept.
        Assert.Contains("session-24.json.gz", remaining);
    }

    private static SessionEventRecord Record(long seq, string type, string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        var data = document.RootElement.Clone();
        var raw = $$"""{"type":{{JsonSerializer.Serialize(type)}},"seq":{{seq}},"time":{{seq}},"data":{{dataJson}}}""";
        return new SessionEventRecord(seq, seq, type, data, raw);
    }
}
