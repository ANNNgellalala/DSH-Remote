using System.Text.Json;
using Xunit;

namespace DshRemote.Core.Tests;

/// <summary>
/// Regression tests for the DSH session-log v4 surface envelope (<c>surfaceOp</c>/<c>sourceEventSeqs</c>/
/// <c>ignorable</c>). The chat transcript only renders append-origin surface events; replacements stay
/// model-only, so the parser must expose the placement instead of silently dropping it.
/// </summary>
public sealed class SurfaceParsingTests
{
    private static SessionEventRecord Parse(string envelopeJson)
    {
        using var document = JsonDocument.Parse(envelopeJson);
        var record = DshParsing.ParseEvent(document.RootElement);
        Assert.NotNull(record);
        return record!;
    }

    [Fact]
    public void AppendSurfaceEvent_ExposesAppendKindAndTranscriptRow()
    {
        var record = Parse(
            """{"type":"tool/result","seq":12,"time":1700000000012,"data":{"turn":1},"surfaceOp":"append","sourceEventSeqs":[11]}""");

        Assert.True(record.IsSurfaceEvent);
        Assert.Equal(SurfaceOpKind.Append, record.SurfaceOp);
        Assert.True(record.IsTranscriptEvent);
        Assert.False(record.IsReplacementSurfaceEvent);
        Assert.Null(record.ReplaceSpan);
        Assert.Equal(new long[] { 11 }, record.SourceEventSeqs!);
        Assert.Equal(11L, record.PrimarySourceSeq);
    }

    [Fact]
    public void PointReplacement_ExposesReplaceKindAndSpan()
    {
        var record = Parse(
            """{"type":"tool/result","seq":2270,"time":1700000002270,"data":{},"surfaceOp":{"op":"replace","startSeq":38,"endSeq":38},"sourceEventSeqs":[38]}""");

        Assert.True(record.IsSurfaceEvent);
        Assert.Equal(SurfaceOpKind.Replace, record.SurfaceOp);
        Assert.True(record.IsReplacementSurfaceEvent);
        Assert.False(record.IsTranscriptEvent);
        Assert.NotNull(record.ReplaceSpan);
        Assert.Equal(38, record.ReplaceSpan!.Value.StartSeq);
        Assert.Equal(38, record.ReplaceSpan.Value.EndSeq);
        Assert.False(record.ReplaceSpan.Value.IsSpan);
    }

    [Fact]
    public void SpanReplacement_EndpointsAreSurfacePositions_AndMayBeNumericDescending()
    {
        // After an earlier replacement landed a high-seq summary node at an old position, start can exceed end.
        var record = Parse(
            """{"type":"user/message","seq":4197,"time":1700000004197,"data":{},"surfaceOp":{"op":"replace","startSeq":2969,"endSeq":18},"sourceEventSeqs":[4195,4196,2969,18]}""");

        Assert.Equal(SurfaceOpKind.Replace, record.SurfaceOp);
        Assert.True(record.IsReplacementSurfaceEvent);
        Assert.NotNull(record.ReplaceSpan);
        Assert.True(record.ReplaceSpan!.Value.IsSpan);
        Assert.Equal(2969, record.ReplaceSpan.Value.StartSeq);
        Assert.Equal(18, record.ReplaceSpan.Value.EndSeq);
        Assert.Equal(4, record.SourceEventSeqs!.Count);
    }

    [Fact]
    public void LegacySurfaceEvent_WithoutSurfaceOp_StaysTranscriptVisible()
    {
        var record = Parse("""{"type":"user/message","seq":5,"time":1700000000005,"data":{"content":[]}}""");

        Assert.True(record.IsSurfaceEvent);
        Assert.Equal(SurfaceOpKind.None, record.SurfaceOp);
        Assert.True(record.IsTranscriptEvent);
        Assert.False(record.IsReplacementSurfaceEvent);
    }

    [Fact]
    public void UnknownSurfaceOpShape_IsTreatedConservativelyAsReplacement()
    {
        var record = Parse(
            """{"type":"tool/result","seq":9,"time":1700000000009,"data":{},"surfaceOp":{"op":"future-op","foo":1}}""");

        Assert.Equal(SurfaceOpKind.Replace, record.SurfaceOp);
        Assert.True(record.IsReplacementSurfaceEvent);
        Assert.False(record.IsTranscriptEvent);
    }

    [Fact]
    public void NonSurfaceEvent_IgnoresSurfaceMetadata()
    {
        var record = Parse(
            """{"type":"tool/call","seq":7,"time":1700000000007,"data":{},"surfaceOp":"append","sourceEventSeqs":[3]}""");

        Assert.False(record.IsSurfaceEvent);
        Assert.Equal(SurfaceOpKind.None, record.SurfaceOp);
        Assert.True(record.IsTranscriptEvent);
    }

    [Fact]
    public void Ignorable_IsParsedAndDefaultsToFalse()
    {
        var ignorable = Parse("""{"type":"future/event","seq":3,"time":1700000000003,"data":{},"ignorable":true}""");
        Assert.True(ignorable.Ignorable);

        var required = Parse("""{"type":"future/event","seq":4,"time":1700000000004,"data":{}}""");
        Assert.False(required.Ignorable);
    }

    [Fact]
    public void FromRawJson_RoundTripsTheSameEnvelopeAsParseEvent()
    {
        const string raw =
            """{"type":"tool/result","seq":2270,"time":1700000002270,"data":{"turn":1,"step":2},"sourceEventSeqs":[38],"surfaceOp":{"op":"replace","startSeq":38,"endSeq":38}}""";

        var fromWire = Parse(raw);
        var fromCache = SessionEventRecord.FromRawJson(raw);

        Assert.NotNull(fromCache);
        Assert.Equal(fromWire.Seq, fromCache!.Seq);
        Assert.Equal(fromWire.Type, fromCache.Type);
        Assert.Equal(fromWire.SurfaceOp, fromCache.SurfaceOp);
        Assert.Equal(fromWire.ReplaceSpan, fromCache.ReplaceSpan);
        Assert.Equal(new long[] { 38 }, fromCache.SourceEventSeqs!);
        Assert.Equal(fromWire.IsSurfaceEvent, fromCache.IsSurfaceEvent);
    }

    [Fact]
    public void FromRawJson_MalformedPayload_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(SessionEventRecord.FromRawJson("not-json"));
        Assert.Null(SessionEventRecord.FromRawJson(string.Empty));
        Assert.Null(SessionEventRecord.FromRawJson("""{"seq":1,"time":2,"data":{}}"""));
    }
}
