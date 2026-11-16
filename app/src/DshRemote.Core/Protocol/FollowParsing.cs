using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Parsed opening snapshot of a <c>session/follow</c> stream.
/// <paramref name="ModelSelection"/> is the session's current model choice read from
/// <c>projections.values.modelSelection</c> (<c>next ?? lastUsed</c>); null for a session that never selected.
/// </summary>
internal sealed record FollowSnapshot(
    IReadOnlyList<SessionEventRecord> Records,
    long Cursor,
    bool HasMore,
    DshModelSelection? ModelSelection = null);

/// <summary>
/// Parsers for the <c>session/follow</c> stream. The stream yields one <c>snapshot</c> opening, then durable
/// <c>event</c> frames, then optionally cursorless <c>assistant-stream</c> frames.
/// </summary>
internal static class FollowParsing
{
    /// <summary>Reads the frame discriminator of a follow stream value.</summary>
    public static string? FrameType(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && DshParsing.GetString(value, "type") is { Length: > 0 } type
            ? type
            : null;

    /// <summary>
    /// Parses the opening <c>{type:'snapshot', cursor, records, hasMore, projections, header}</c> frame.
    /// Only the fields the phone needs are read; <c>header</c> is ignored and <c>projections</c> is read only for
    /// the session model selection (<c>values.modelSelection.next ?? lastUsed</c>).
    /// </summary>
    public static bool TryParseSnapshot(JsonElement value, out FollowSnapshot snapshot)
    {
        snapshot = null!;
        if (value.ValueKind != JsonValueKind.Object ||
            !string.Equals(DshParsing.GetString(value, "type"), "snapshot", StringComparison.Ordinal))
        {
            return false;
        }

        var records = new List<SessionEventRecord>();
        if (value.TryGetProperty("records", out var recordsElement) && recordsElement.ValueKind == JsonValueKind.Array)
        {
            records.AddRange(ParseEventRecords(recordsElement));
        }

        records.Sort((left, right) => left.Seq.CompareTo(right.Seq));
        var cursor = DshParsing.GetInt64(value, "cursor") ?? (records.Count > 0 ? records[^1].Seq : 0);
        var hasMore = DshParsing.GetBool(value, "hasMore") ?? false;
        DshModelSelection? modelSelection = null;
        if (DshParsing.TryGetObject(value, "projections") is { } projections)
        {
            modelSelection = DshParsing.TryReadModelSelection(DshParsing.TryGetObject(projections, "values"));
        }

        snapshot = new FollowSnapshot(records, cursor, hasMore, modelSelection);
        return true;
    }

    /// <summary>Parses one durable <c>{type:'event', event:{…}}</c> follow frame.</summary>
    public static bool TryParseEventFrame(JsonElement value, out SessionEventRecord record)
    {
        record = null!;
        if (value.ValueKind != JsonValueKind.Object ||
            !string.Equals(DshParsing.GetString(value, "type"), "event", StringComparison.Ordinal))
        {
            return false;
        }

        if (!value.TryGetProperty("event", out var eventElement))
        {
            return false;
        }

        var parsed = DshParsing.ParseEvent(eventElement);
        if (parsed is null)
        {
            return false;
        }

        record = parsed;
        return true;
    }

    /// <summary>
    /// Parses a <c>session/events</c>/snapshot <c>records</c> array of <c>{type:'event',event}</c> entries.
    /// Unknown record kinds are skipped.
    /// </summary>
    public static IReadOnlyList<SessionEventRecord> ParseEventRecords(JsonElement recordsElement)
    {
        var records = new List<SessionEventRecord>();
        if (recordsElement.ValueKind != JsonValueKind.Array)
        {
            return records;
        }

        foreach (var record in recordsElement.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object ||
                !string.Equals(DshParsing.GetString(record, "type"), "event", StringComparison.Ordinal) ||
                !record.TryGetProperty("event", out var eventElement))
            {
                continue;
            }

            var parsed = DshParsing.ParseEvent(eventElement);
            if (parsed is not null)
            {
                records.Add(parsed);
            }
        }

        return records;
    }

    /// <summary>
    /// True when <paramref name="records"/> (ascending) covers every seq after <paramref name="afterSeq"/>
    /// up to its last seq without a hole. Sequence numbers are contiguous in a Session log.
    /// </summary>
    public static bool IsContiguous(IReadOnlyList<SessionEventRecord> records, long afterSeq)
    {
        var expected = afterSeq + 1;
        foreach (var record in records)
        {
            if (record.Seq < expected)
            {
                continue;
            }

            if (record.Seq != expected)
            {
                return false;
            }

            expected++;
        }

        return true;
    }

    /// <summary>Merges two event sequences, de-duplicating by seq and ordering ascending.</summary>
    public static List<SessionEventRecord> MergeBySeq(
        IEnumerable<SessionEventRecord> first,
        IEnumerable<SessionEventRecord> second)
    {
        var bySeq = new SortedDictionary<long, SessionEventRecord>();
        foreach (var record in first)
        {
            bySeq[record.Seq] = record;
        }

        foreach (var record in second)
        {
            bySeq[record.Seq] = record;
        }

        return bySeq.Values.ToList();
    }
}
