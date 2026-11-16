using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class FollowParsingTests
{
    [Fact]
    public void FollowArgs_WrapRequestAndUseLiteralAssistantStream()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.Follow("session-1", 40)));
        var request = document.RootElement.GetProperty("request");
        Assert.Equal("session", request.GetProperty("address").GetProperty("kind").GetString());
        Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
        Assert.Equal(40, request.GetProperty("maxMessages").GetInt32());
        Assert.Equal(JsonValueKind.True, request.GetProperty("assistantStream").ValueKind);
    }

    [Fact]
    public void UpdateQueueArgs_MatchStrictSchema()
    {
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.UpdateQueue("s1", "item-1", "edit", "new text"))))
        {
            var request = document.RootElement.GetProperty("request");
            Assert.Equal(new[] { "sessionId", "itemId", "action" }, request.EnumerateObject().Select(p => p.Name));
            var action = request.GetProperty("action");
            Assert.Equal("edit", action.GetProperty("kind").GetString());
            var content = action.GetProperty("content");
            Assert.Equal(1, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("new text", content[0].GetProperty("text").GetString());
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.UpdateQueue("s1", "item-1", "remove", null))))
        {
            var action = document.RootElement.GetProperty("request").GetProperty("action");
            Assert.Equal(new[] { "kind" }, action.EnumerateObject().Select(p => p.Name));
            Assert.Equal("remove", action.GetProperty("kind").GetString());
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.UpdateQueue("s1", "item-1", "steer", null))))
        {
            Assert.Equal("steer", document.RootElement.GetProperty("request").GetProperty("action").GetProperty("kind").GetString());
        }

        Assert.Throws<ArgumentException>(() => DshArgs.UpdateQueue("s1", "item-1", "edit", null));
        Assert.Throws<ArgumentException>(() => DshArgs.UpdateQueue("s1", "", "remove", null));
        Assert.Throws<ArgumentException>(() => DshArgs.UpdateQueue("s1", "item-1", "bogus", null));
    }

    [Fact]
    public void Snapshot_ParsesRecordsCursorAndIgnoresHeaderProjections()
    {
        const string json = """
        {
          "type": "snapshot",
          "header": { "version": 1, "id": "session-1", "createdAt": 1, "isSeeded": false },
          "cursor": 5,
          "hasMore": true,
          "records": [
            { "type": "event", "event": { "type": "assistant/message", "seq": 5, "time": 50, "data": {} } },
            { "type": "event", "event": { "type": "turn/start", "seq": 4, "time": 40, "data": { "turn": 1 } } },
            { "type": "assistant-stream", "value": {} }
          ],
          "projections": { "asOfSeq": 5, "values": { "title": "ignored" } }
        }
        """;

        using var document = JsonDocument.Parse(json);
        Assert.True(FollowParsing.TryParseSnapshot(document.RootElement, out var snapshot));
        Assert.Equal(5, snapshot.Cursor);
        Assert.True(snapshot.HasMore);
        Assert.Equal(new long[] { 4, 5 }, snapshot.Records.Select(r => r.Seq));
        Assert.Equal(new[] { "turn/start", "assistant/message" }, snapshot.Records.Select(r => r.Type));
    }

    [Fact]
    public void FollowFrames_DistinguishEventAssistantStreamAndSnapshot()
    {
        using var eventFrame = JsonDocument.Parse(
            """{"type":"event","event":{"type":"user/message","seq":7,"time":70,"data":{"message":{"content":[{"type":"text","text":"hi"}]}}}}""");
        Assert.Equal("event", FollowParsing.FrameType(eventFrame.RootElement));
        Assert.True(FollowParsing.TryParseEventFrame(eventFrame.RootElement, out var record));
        Assert.Equal(7, record.Seq);
        Assert.Equal("hi", record.TextOf());
        Assert.False(FollowParsing.TryParseSnapshot(eventFrame.RootElement, out _));

        using var streamFrame = JsonDocument.Parse(
            """{"type":"assistant-stream","frame":{"type":"chunk","attemptId":"a","revision":1,"index":2,"time":1,"chunk":{"type":"text-delta","text":"x"}}}""");
        Assert.Equal("assistant-stream", FollowParsing.FrameType(streamFrame.RootElement));
        Assert.False(FollowParsing.TryParseEventFrame(streamFrame.RootElement, out _));
        Assert.False(FollowParsing.TryParseSnapshot(streamFrame.RootElement, out _));
    }

    [Fact]
    public void Snapshot_ParsesModelSelectionNextThenLastUsed()
    {
        static string SnapshotWithSelection(string selectionJson) =>
            "{\"type\":\"snapshot\",\"cursor\":2,\"hasMore\":false,\"records\":[],"
            + "\"projections\":{\"asOfSeq\":2,\"values\":{\"title\":\"t\",\"modelSelection\":"
            + selectionJson
            + "}}}";

        using (var document = JsonDocument.Parse(SnapshotWithSelection(
            """{"lastUsed":{"provider":"p-old","model":"m-old"},"next":{"provider":"p-new","model":"m-new","reasoningEffort":"max"}}""")))
        {
            Assert.True(FollowParsing.TryParseSnapshot(document.RootElement, out var snapshot));
            Assert.NotNull(snapshot.ModelSelection);
            Assert.Equal("p-new", snapshot.ModelSelection!.Provider);
            Assert.Equal("m-new", snapshot.ModelSelection.Model);
            Assert.Equal("max", snapshot.ModelSelection.ReasoningEffort);
        }

        // `next` is the wire view of pending ?? lastUsed: when null/absent, lastUsed must win.
        using (var document = JsonDocument.Parse(SnapshotWithSelection(
            """{"lastUsed":{"provider":"p-old","model":"m-old","reasoningEffort":"high"},"next":null}""")))
        {
            Assert.True(FollowParsing.TryParseSnapshot(document.RootElement, out var snapshot));
            Assert.NotNull(snapshot.ModelSelection);
            Assert.Equal("p-old", snapshot.ModelSelection!.Provider);
            Assert.Equal("high", snapshot.ModelSelection.ReasoningEffort);
        }

        // A brand-new session reports both as null: no selection.
        using (var document = JsonDocument.Parse(SnapshotWithSelection("""{"lastUsed":null,"next":null}""")))
        {
            Assert.True(FollowParsing.TryParseSnapshot(document.RootElement, out var snapshot));
            Assert.Null(snapshot.ModelSelection);
        }

        // Snapshots without projections at all (older payloads) stay null too.
        using (var document = JsonDocument.Parse("""{"type":"snapshot","cursor":2,"records":[]}"""))
        {
            Assert.True(FollowParsing.TryParseSnapshot(document.RootElement, out var snapshot));
            Assert.Null(snapshot.ModelSelection);
        }
    }

    [Fact]
    public void Contiguity_DetectsHolesAndDuplicates()
    {
        static SessionEventRecord Event(long seq) =>
            new(seq, seq * 10, "turn/start", JsonDocument.Parse("{}").RootElement, "{}");

        Assert.True(FollowParsing.IsContiguous(new[] { Event(3), Event(4), Event(5) }, 2));
        Assert.True(FollowParsing.IsContiguous(new[] { Event(2), Event(3) }, 1));
        Assert.False(FollowParsing.IsContiguous(new[] { Event(3), Event(5) }, 2));
        Assert.False(FollowParsing.IsContiguous(new[] { Event(5), Event(6) }, 2));
        Assert.True(FollowParsing.IsContiguous(Array.Empty<SessionEventRecord>(), 9));

        var merged = FollowParsing.MergeBySeq(new[] { Event(3), Event(5) }, new[] { Event(4), Event(5), Event(6) });
        Assert.Equal(new long[] { 3, 4, 5, 6 }, merged.Select(r => r.Seq));
        Assert.Equal("turn/start", merged[1].Type);
    }
}
