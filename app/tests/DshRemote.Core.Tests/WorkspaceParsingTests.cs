using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class WorkspaceParsingTests
{
    private const string BaselineJson = """
    {
      "type": "baseline",
      "value": {
        "items": [
          {
            "workspaceId": "ws-1",
            "path": "D:\\Code\\VibeCoding\\DSHRemote",
            "title": "DSHRemote",
            "sessionIds": ["session-a", "session-b"],
            "createdAt": "2026-11-08T10:00:00.000Z",
            "updatedAt": "2026-11-09T02:30:00.000Z"
          },
          {
            "workspaceId": "ws-2",
            "path": "C:\\Users\\user\\Downloads\\日常对话",
            "title": "日常对话",
            "sessionIds": [],
            "createdAt": 1794000000000,
            "updatedAt": "not-a-date"
          }
        ],
        "archivedSessionIds": ["session-b"],
        "pinnedSessionIds": ["session-a"],
        "futureField": {"ignored": true}
      }
    }
    """;

    [Fact]
    public void Baseline_ParsesWorkspacesAndNormalizesIsoTimestamps()
    {
        using var document = JsonDocument.Parse(BaselineJson);
        var frame = WorkspaceParsing.ParseFrame(document.RootElement);

        Assert.NotNull(frame);
        Assert.Equal(WorkspaceFrameKind.Baseline, frame!.Kind);
        var state = frame.State;
        Assert.NotNull(state);
        Assert.Equal(2, state!.Workspaces.Count);
        Assert.Equal(new[] { "session-a", "session-b" }, state.Workspaces[0].SessionIds);
        Assert.Equal("session-b", Assert.Single(state.ArchivedSessionIds));
        Assert.Equal("session-a", Assert.Single(state.PinnedSessionIds));

        var first = state.Workspaces[0];
        Assert.Equal("ws-1", first.WorkspaceId);
        Assert.Equal(@"D:\Code\VibeCoding\DSHRemote", first.Path);
        Assert.Equal("DSHRemote", first.Title);
        Assert.Equal(1794132000000, first.CreatedAt);
        Assert.Equal(1794191400000, first.UpdatedAt);

        var second = state.Workspaces[1];
        Assert.Equal(1794000000000, second.CreatedAt);
        Assert.Equal(0, second.UpdatedAt); // unparsable timestamp is tolerated as 0
    }

    [Fact]
    public void IncrementFrames_ParseEachKind()
    {
        static WorkspaceFrame? Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return WorkspaceParsing.ParseFrame(document.RootElement);
        }

        var replace = Parse("""
        {"type":"replace","value":{"items":[],"archivedSessionIds":["s1"],"pinnedSessionIds":[]}}
        """);
        Assert.Equal(WorkspaceFrameKind.Replace, replace!.Kind);
        Assert.Equal("s1", Assert.Single(replace.State!.ArchivedSessionIds));

        var upsert = Parse("""
        {"type":"upsert","workspace":{"workspaceId":"ws-3","path":"/tmp","title":"T","sessionIds":["s9"],
         "createdAt":"2026-11-09T00:00:00Z","updatedAt":"2026-11-09T01:00:00Z"}}
        """);
        Assert.Equal(WorkspaceFrameKind.Upsert, upsert!.Kind);
        Assert.Equal("ws-3", upsert.Workspace!.WorkspaceId);
        Assert.Equal("s9", Assert.Single(upsert.Workspace.SessionIds));

        var remove = Parse("""{"type":"remove","workspaceId":"ws-1"}""");
        Assert.Equal(WorkspaceFrameKind.Remove, remove!.Kind);
        Assert.Equal("ws-1", remove.WorkspaceId);

        var order = Parse("""{"type":"order","workspaceIds":["a","b"]}""");
        Assert.Equal(WorkspaceFrameKind.Order, order!.Kind);
        Assert.Equal(new[] { "a", "b" }, order.Ids);

        var archived = Parse("""{"type":"archived","archivedSessionIds":["s2","s3"]}""");
        Assert.Equal(WorkspaceFrameKind.Archived, archived!.Kind);
        Assert.Equal(new[] { "s2", "s3" }, archived.Ids);

        var pinned = Parse("""{"type":"pinned","pinnedSessionIds":["s4"]}""");
        Assert.Equal(WorkspaceFrameKind.Pinned, pinned!.Kind);
        Assert.Equal(new[] { "s4" }, pinned.Ids);
    }

    [Fact]
    public void MalformedFrames_AreRejectedNotThrown()
    {
        static WorkspaceFrame? Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return WorkspaceParsing.ParseFrame(document.RootElement);
        }

        Assert.Null(Parse("{}"));
        Assert.Null(Parse("""{"type":"baseline"}"""));
        // An empty baseline value is structurally valid: tolerate an empty state rather than failing the stream.
        var emptyBaseline = Parse("""{"type":"baseline","value":{}}""");
        Assert.NotNull(emptyBaseline);
        Assert.Empty(emptyBaseline!.State!.Workspaces);
        Assert.Null(Parse("""{"type":"upsert","workspace":{}}"""));
        Assert.Null(Parse("""{"type":"remove"}"""));
        Assert.Null(Parse("""{"type":"mystery"}"""));
        Assert.Null(Parse("[]"));
    }

    [Fact]
    public void MutationResults_ParseArchivedIdsAndCreatedSession()
    {
        using var archived = JsonDocument.Parse("""{"archivedSessionIds":["session-1","session-2"]}""");
        Assert.Equal(new[] { "session-1", "session-2" }, WorkspaceParsing.ParseArchivedIds(archived.RootElement));

        using var created = JsonDocument.Parse("""{"sessionId":"session-new","agentPreset":"standard"}""");
        Assert.Equal("session-new", WorkspaceParsing.ParseCreatedSessionId(created.RootElement));

        using var missing = JsonDocument.Parse("""{"agentPreset":"standard"}""");
        var exception = Assert.Throws<DshRemoteException>(() => WorkspaceParsing.ParseCreatedSessionId(missing.RootElement));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, exception.Code);
    }

    [Fact]
    public void AttachmentResult_ParsesResponseRefAndBase64Bytes()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var base64 = Convert.ToBase64String(bytes);
        var json = """
        {
          "attachment": {
            "attachmentId": "sha256:abc123",
            "mediaType": "image/png",
            "bytes": 7,
            "width": 32,
            "height": 16,
            "name": "shot.png",
            "originalDimensions": {"width": 64, "height": 32}
          },
          "data": "BASE64"
        }
        """.Replace("BASE64", base64);

        var fallback = new DshImageRef("sha256:abc123", "image/png", 7, 32, 16);
        using var document = JsonDocument.Parse(json);
        var image = WorkspaceParsing.ParseAttachmentResult(document.RootElement, fallback);

        Assert.Equal(bytes, image.Bytes);
        Assert.Equal("image/png", image.Image.MediaType);
        Assert.Equal("shot.png", image.Image.Name);
        Assert.Equal(7, image.Image.Bytes);
        Assert.Equal(32, image.Image.Width);

        // Missing data is a protocol error.
        using var noData = JsonDocument.Parse("""{"attachment":{}}""");
        var exception = Assert.Throws<DshRemoteException>(() => WorkspaceParsing.ParseAttachmentResult(noData.RootElement, fallback));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, exception.Code);

        // Invalid base64 is a protocol error.
        using var badData = JsonDocument.Parse("""{"attachment":{},"data":"@@not-base64@@"}""");
        Assert.Throws<DshRemoteException>(() => WorkspaceParsing.ParseAttachmentResult(badData.RootElement, fallback));
    }
}
