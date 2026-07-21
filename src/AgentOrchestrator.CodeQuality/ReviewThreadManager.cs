using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Preserves review conversations and relocates their line anchors after code drift.</summary>
public static class ReviewThreadManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim GetWriteLock(string metaPath) =>
        WriteLocks.GetOrAdd(Path.GetFullPath(metaPath), _ => new SemaphoreSlim(1, 1));

    public static JsonArray LoadAndHeal(string metaPath, string relativePath, string content)
    {
        if (!File.Exists(metaPath)) return [];
        var root = JsonNode.Parse(File.ReadAllText(metaPath))?.AsObject();
        var stored = root?["threads"] as JsonArray;
        if (stored is null) return [];

        var threads = (JsonArray)stored.DeepClone();
        foreach (var node in threads)
        {
            if (node is not JsonObject thread || thread["anchor"] is not JsonObject anchor ||
                !string.Equals(anchor["path"]?.GetValue<string>(), relativePath, StringComparison.Ordinal)) continue;
            HealAnchor(thread, anchor, content);
        }
        return threads;
    }

    public static void AppendAgentUpdates(JsonArray threads, JsonObject response, string agent, string model, DateTimeOffset now)
    {
        if (response["threadUpdates"] is not JsonArray updates) return;
        var byId = threads.OfType<JsonObject>().ToDictionary(thread => thread["id"]!.GetValue<string>(), StringComparer.Ordinal);
        foreach (var node in updates)
        {
            var update = node!.AsObject();
            var threadId = update["threadId"]!.GetValue<string>();
            if (!byId.TryGetValue(threadId, out var thread)) continue;
            var entries = thread["entries"]!.AsArray();
            var entry = new JsonObject
            {
                ["id"] = $"entry-{Guid.NewGuid():N}",
                ["author"] = new JsonObject { ["kind"] = "agent", ["agent"] = agent, ["model"] = model },
                ["createdAt"] = now.UtcDateTime.ToString("O"),
                ["body"] = update["body"]!.GetValue<string>().Trim(),
            };
            if (update["replyTo"] is JsonNode replyTo) entry["replyTo"] = replyTo.DeepClone();
            entries.Add(entry);
            if (update["status"] is JsonValue status) thread["status"] = status.DeepClone();
        }
    }

    public static JsonArray MergeLatest(JsonArray promptedSnapshot, string metaPath, string relativePath, string content)
    {
        if (!File.Exists(metaPath)) return promptedSnapshot;
        var latest = LoadAndHeal(metaPath, relativePath, content);
        var result = (JsonArray)promptedSnapshot.DeepClone();
        var byId = result.OfType<JsonObject>().ToDictionary(thread => thread["id"]!.GetValue<string>(), StringComparer.Ordinal);
        foreach (var latestThread in latest.OfType<JsonObject>())
        {
            var id = latestThread["id"]!.GetValue<string>();
            if (!byId.TryGetValue(id, out var existing))
            {
                var clone = latestThread.DeepClone().AsObject();
                result.Add(clone); byId[id] = clone; continue;
            }
            existing["anchor"] = latestThread["anchor"]!.DeepClone();
            if (latestThread["anchorState"] is JsonNode anchorState) existing["anchorState"] = anchorState.DeepClone();
            else existing.Remove("anchorState");
            if (latestThread["healedAt"] is JsonNode healedAt) existing["healedAt"] = healedAt.DeepClone();
            else existing.Remove("healedAt");
            existing["status"] = latestThread["status"]!.DeepClone();
            var entries = existing["entries"]!.AsArray();
            var entryIds = entries.OfType<JsonObject>().Select(entry => entry["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in latestThread["entries"]!.AsArray().OfType<JsonObject>())
                if (entryIds.Add(entry["id"]!.GetValue<string>())) entries.Add(entry.DeepClone());
        }
        return result;
    }

    public static void HealFromFindingFingerprints(JsonArray threads, JsonObject response, string relativePath, string content)
    {
        if (response["findings"] is not JsonArray findings) return;
        var byFingerprint = findings.OfType<JsonObject>()
            .Where(finding => finding["fingerprint"] is JsonValue)
            .GroupBy(finding => finding["fingerprint"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var thread in threads.OfType<JsonObject>().Where(candidate => candidate["anchorState"]?.GetValue<string>() == "detached"))
        {
            var anchor = thread["anchor"]!.AsObject();
            if (!byFingerprint.TryGetValue(anchor["fingerprint"]!.GetValue<string>(), out var finding) ||
                finding["locations"] is not JsonArray locations) continue;
            var location = locations.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["path"]?.GetValue<string>() == relativePath && candidate["range"] is JsonObject);
            if (location is null || !TryRange(location["range"], out var range)) continue;
            anchor["lastKnownRange"] = location["range"]!.DeepClone();
            anchor["contextHash"] = ComputeContextHash(content, range);
            thread["anchorState"] = "healed";
            thread["healedAt"] = DateTime.UtcNow.ToString("O");
        }
    }

    public static string ComputeContextHash(string content, FindingRange range)
    {
        var lines = NormalizeLines(content);
        var start = Math.Clamp(range.Start.Line - 1, 0, Math.Max(0, lines.Length - 1));
        var end = Math.Clamp(range.End.Line - 1, start, Math.Max(start, lines.Length - 1));
        return Hash(string.Join('\n', lines[start..(end + 1)]));
    }

    private static void HealAnchor(JsonObject thread, JsonObject anchor, string content)
    {
        if (!TryRange(anchor["lastKnownRange"], out var range))
        {
            thread["anchorState"] = "detached";
            return;
        }
        var expected = anchor["contextHash"]?.GetValue<string>();
        var lines = NormalizeLines(content);
        var span = Math.Max(1, range.End.Line - range.Start.Line + 1);
        var oldStart = range.Start.Line;
        var matches = new List<int>();
        for (var start = 1; start + span - 1 <= lines.Length; start++)
        {
            var candidate = new FindingRange(new FindingPosition(start, range.Start.Column),
                new FindingPosition(start + span - 1, range.End.Column));
            if (string.Equals(ComputeContextHash(content, candidate), expected, StringComparison.Ordinal)) matches.Add(start);
        }
        if (matches.Count == 0)
        {
            thread["anchorState"] = "detached";
            return;
        }
        var nearest = matches.MinBy(line => Math.Abs(line - oldStart));
        if (nearest == oldStart)
        {
            thread["anchorState"] = "anchored";
            return;
        }
        anchor["lastKnownRange"] = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = nearest, ["column"] = range.Start.Column },
            ["end"] = new JsonObject { ["line"] = nearest + span - 1, ["column"] = range.End.Column },
        };
        thread["anchorState"] = "healed";
        thread["healedAt"] = DateTime.UtcNow.ToString("O");
    }

    private static bool TryRange(JsonNode? node, out FindingRange range)
    {
        range = default!;
        try
        {
            var value = node!.AsObject();
            var start = value["start"]!.AsObject();
            var end = value["end"]!.AsObject();
            range = new FindingRange(
                new FindingPosition(start["line"]!.GetValue<int>(), start["column"]!.GetValue<int>()),
                new FindingPosition(end["line"]!.GetValue<int>(), end["column"]!.GetValue<int>()));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            return false;
        }
    }

    private static string[] NormalizeLines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    private static string Hash(string value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
