using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class ParsingTests
{
    // Real runtime sample (session/list item) captured from the desktop DSH process.
    private const string SessionListJson = """
    {
      "items": [
        {
          "sessionId": "session-82ed888f-d909-4c8b-a769-81161c051533",
          "updatedAt": 1794154489599,
          "agentAvailable": true,
          "formatStatus": "current",
          "running": true,
          "blank": false,
          "cwd": "C:\\Users\\user\\Downloads\\日常对话",
          "projections": {
            "kind": "sequenced",
            "asOfSeq": 27,
            "values": {
              "title": "排查DSH运行系统卡顿",
              "modelSelection": {
                "lastUsed": { "provider": "opencode-go", "model": "deepseek-v4.1-flash", "reasoningEffort": "high" },
                "next": { "provider": "opencode-go", "model": "deepseek-v4.1-flash", "reasoningEffort": "high" }
              },
              "workingDirectory": "C:\\Users\\user\\Downloads\\日常对话",
              "sessionListMetadata": { "blank": false, "lastPromptAt": 1794154489599 },
              "someFutureField": { "nested": [1, 2, 3] }
            }
          },
          "unknownTopLevelField": "ignored"
        },
        {
          "sessionId": "session-blank",
          "updatedAt": 1700000000000,
          "agentAvailable": false,
          "running": false,
          "blank": true
        }
      ]
    }
    """;

    [Fact]
    public void SessionList_ParsesRealRuntimeShapeAndToleratesUnknownFields()
    {
        using var document = JsonDocument.Parse(SessionListJson);
        var sessions = DshParsing.ParseSessionList(document.RootElement);

        Assert.Equal(2, sessions.Count);

        var first = sessions[0];
        Assert.Equal("session-82ed888f-d909-4c8b-a769-81161c051533", first.SessionId);
        Assert.Equal("排查DSH运行系统卡顿", first.Title);
        Assert.Equal(@"C:\Users\user\Downloads\日常对话", first.Cwd);
        Assert.Equal("deepseek-v4.1-flash", first.Model);
        Assert.True(first.Running);
        Assert.True(first.AgentAvailable);
        Assert.False(first.Blank);
        Assert.Equal(1794154489599, first.UpdatedAt);
        Assert.Equal(1794154489599, first.LastPromptAt);

        var second = sessions[1];
        Assert.True(second.Blank);
        Assert.Equal(1700000000000, second.LastPromptAt);
        Assert.Null(second.Title);

        // Top-level sessions carry no child lineage.
        Assert.Null(first.ParentSessionId);
        Assert.Null(first.Origin);
        Assert.Null(second.ParentSessionId);
        Assert.Null(second.Origin);
    }

    [Fact]
    public void SessionList_ParsesSubagentLineageFields()
    {
        // Real runtime shape: a child session is a bare uuid with parentSessionId + origin:'subagent'.
        const string json = """
        {"items":[
          {"sessionId":"683d1f74-3064-4bba-8b5f-d56a57bbfcc1","updatedAt":1794160000000,
           "running":false,"agentAvailable":true,"blank":false,
           "parentSessionId":"session-82ed888f-d909-4c8b-a769-81161c051533","origin":"subagent",
           "projections":{"kind":"sequenced","asOfSeq":3,
             "values":{"title":"You are researching technical feasibility","sessionListMetadata":{"blank":false,"lastPromptAt":1794160000000}}}},
          {"sessionId":"session-82ed888f-d909-4c8b-a769-81161c051533","updatedAt":1794159999000,
           "running":true,"agentAvailable":true,"blank":false,"origin":"user","unknown":"ignored"}
        ]}
        """;

        using var document = JsonDocument.Parse(json);
        var sessions = DshParsing.ParseSessionList(document.RootElement);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("683d1f74-3064-4bba-8b5f-d56a57bbfcc1", sessions[0].SessionId);
        Assert.Equal("session-82ed888f-d909-4c8b-a769-81161c051533", sessions[0].ParentSessionId);
        Assert.Equal("subagent", sessions[0].Origin);

        Assert.Equal("session-82ed888f-d909-4c8b-a769-81161c051533", sessions[1].SessionId);
        Assert.Null(sessions[1].ParentSessionId);
        Assert.Equal("user", sessions[1].Origin);
    }

    [Fact]
    public void SessionList_RejectsMissingItems()
    {
        using var document = JsonDocument.Parse("""{"nope":[]}""");
        var exception = Assert.Throws<DshRemoteException>(() => DshParsing.ParseSessionList(document.RootElement));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, exception.Code);
    }

    [Fact]
    public void History_ParsesRecordsCursorAndSkipsUnknownKinds()
    {
        const string json = """
        {
          "cursor": 12,
          "hasMore": true,
          "records": [
            { "type": "event", "event": { "type": "user/message", "seq": 10, "time": 1700000000010,
              "data": { "message": { "role": "user", "content": [{"type":"text","text":"你好"}] } } } },
            { "type": "event", "event": { "type": "assistant/message", "seq": 11, "time": 1700000000011,
              "data": { "message": { "role": "assistant",
                "content": [{"type":"text","text":"第一行"},{"type":"text","text":"第二行"},{"type":"tool-call","name":"pwsh"}] } } } },
            { "type": "assistant-stream", "value": { "delta": "ignored" } },
            { "type": "event", "event": { "type": "turn/end", "seq": 12, "time": 1700000000012, "data": { "reason": {"kind":"completed"} } } }
          ]
        }
        """;

        using var document = JsonDocument.Parse(json);
        var page = DshParsing.ParseHistoryPage(document.RootElement);

        Assert.True(page.HasMore);
        Assert.Equal(12, page.Cursor);
        Assert.Equal(3, page.Records.Count);
        Assert.Equal(new[] { "user/message", "assistant/message", "turn/end" }, page.Records.Select(r => r.Type));
        Assert.Equal(10, page.Records[0].Seq);
        Assert.Equal("你好", page.Records[0].TextOf());
        Assert.Equal("第一行\n第二行", page.Records[1].TextOf());
        Assert.Contains("\"seq\":11", page.Records[1].RawJson.Replace(" ", ""));
    }

    [Fact]
    public void History_EmptyPageIsValid()
    {
        using var document = JsonDocument.Parse("""{"cursor":0,"records":[],"hasMore":false}""");
        var page = DshParsing.ParseHistoryPage(document.RootElement);
        Assert.Empty(page.Records);
        Assert.False(page.HasMore);
        Assert.Equal(0, page.Cursor);
    }

    [Fact]
    public void State_ParsesQueueAndPendingInput()
    {
        const string json = """
        {
          "cursor": 42,
          "active": true,
          "running": true,
          "pendingInput": ["approval", "other"],
          "queue": [
            { "id": "item-1", "placement": "queued", "rpcId": "r1",
              "content": [{"type":"text","text":"排队的问题"}] },
            { "id": "item-2", "placement": "steering", "content": [{"type":"text","text":"steer"}] }
          ]
        }
        """;

        using var document = JsonDocument.Parse(json);
        var state = DshParsing.ParseState(document.RootElement);

        Assert.Equal(42, state.Cursor);
        Assert.True(state.Active);
        Assert.True(state.Running);
        Assert.Equal(2, state.Queue.Count);
        Assert.Equal("item-1", state.Queue[0].Id);
        Assert.Equal("queued", state.Queue[0].Placement);
        Assert.Equal("排队的问题", state.Queue[0].Text);
        Assert.Equal(new[] { "approval", "other" }, state.PendingInput);
    }

    [Fact]
    public void State_NullQueueIsEmpty()
    {
        using var document = JsonDocument.Parse("""{"cursor":1,"active":false,"running":false,"queue":null,"pendingInput":[]}""");
        var state = DshParsing.ParseState(document.RootElement);
        Assert.Empty(state.Queue);
        Assert.Empty(state.PendingInput);
    }

    [Fact]
    public void ObserveValue_ParsesReadyThenEventFrames()
    {
        using var ready = JsonDocument.Parse("""{"type":"ready"}""");
        Assert.False(DshParsing.TryParseObserveValue(ready.RootElement, out _));

        using var item = JsonDocument.Parse("""
        {"type":"event","event":{"type":"turn/start","seq":4,"time":1700000000004,"data":{"turn":1}}}
        """);
        Assert.True(DshParsing.TryParseObserveValue(item.RootElement, out var record));
        Assert.Equal("turn/start", record.Type);
        Assert.Equal(4, record.Seq);
        Assert.Equal(1700000000004, record.Time);
        Assert.Equal(1, record.Data.GetProperty("turn").GetInt32());

        using var raw = JsonDocument.Parse("""{"type":"step/start","seq":5,"time":6,"data":{}}""");
        Assert.True(DshParsing.TryParseObserveValue(raw.RootElement, out var rawRecord));
        Assert.Equal("step/start", rawRecord.Type);

        using var unknown = JsonDocument.Parse("""{"type":"mystery"}""");
        Assert.False(DshParsing.TryParseObserveValue(unknown.RootElement, out _));
    }
}
