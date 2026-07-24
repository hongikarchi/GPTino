using System.Collections.Concurrent;
using System.Text.Json;

namespace GPTino.AgentHost.Runtime;

public sealed record SessionRateLimitWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// Latest token accounting for one session's codex thread. ContextUsedTokens is the footprint of
/// the most recent request (what currently occupies the context window); TotalTokens accumulates
/// across the thread.
/// </summary>
public sealed record SessionUsageSnapshot(
    long? TotalTokens,
    long? ContextWindow,
    long? ContextUsedTokens,
    IReadOnlyList<SessionRateLimitWindow> RateLimits);

/// <summary>
/// In-memory per-session usage, fed from codex app-server notifications and projected to the
/// panel. Mirrors the EffectiveModelState pattern: ephemeral, rebuilt as turns run.
/// </summary>
public sealed class SessionUsageState
{
    private readonly ConcurrentDictionary<Guid, SessionUsageSnapshot> _snapshots = new();

    public bool TryGet(Guid sessionId, out SessionUsageSnapshot snapshot)
    {
        if (_snapshots.TryGetValue(sessionId, out var found))
        {
            snapshot = found;
            return true;
        }
        snapshot = default!;
        return false;
    }

    /// <summary>Merges a parsed update into the session's snapshot; null fields keep prior values.</summary>
    public bool Update(Guid sessionId, SessionUsageSnapshot update)
    {
        var changed = false;
        _snapshots.AddOrUpdate(
            sessionId,
            _ =>
            {
                changed = true;
                return update;
            },
            (_, existing) =>
            {
                // Reuse the existing list when the values match so change detection compares by
                // value, not list reference — otherwise every rate-limit-carrying event would
                // count as a change and trigger a full-state SSE publish.
                var rateLimits = update.RateLimits.Count > 0 && !update.RateLimits.SequenceEqual(existing.RateLimits)
                    ? update.RateLimits
                    : existing.RateLimits;
                var merged = new SessionUsageSnapshot(
                    update.TotalTokens ?? existing.TotalTokens,
                    update.ContextWindow ?? existing.ContextWindow,
                    update.ContextUsedTokens ?? existing.ContextUsedTokens,
                    rateLimits);
                changed = merged.TotalTokens != existing.TotalTokens ||
                    merged.ContextWindow != existing.ContextWindow ||
                    merged.ContextUsedTokens != existing.ContextUsedTokens ||
                    !ReferenceEquals(rateLimits, existing.RateLimits);
                return changed ? merged : existing;
            });
        return changed;
    }

    /// <summary>
    /// Tolerant parser for codex app-server usage payloads. The CLI's exact notification shape is
    /// version-dependent (snake_case core events vs camelCase app-server serialization), so every
    /// field is probed under both spellings and absent fields stay null. Returns null when the
    /// element carries nothing usage-shaped at all.
    /// </summary>
    public static SessionUsageSnapshot? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // token-count events nest the totals under "info"; turn/completed carries "usage" directly.
        var info = Probe(element, "info") ?? element;
        var total = Probe(info, "totalTokenUsage", "total_token_usage");
        var last = Probe(info, "lastTokenUsage", "last_token_usage");
        var direct = Probe(element, "usage") ?? (total is null && last is null ? info : (JsonElement?)null);

        var totalTokens = ReadTokens(total) ?? ReadTokens(direct);
        var contextUsed = ReadTokens(last);
        var contextWindow =
            ReadLong(info, "modelContextWindow", "model_context_window") ??
            ReadLong(element, "modelContextWindow", "model_context_window");

        var rateLimits = new List<SessionRateLimitWindow>();
        var limits = Probe(element, "rateLimits", "rate_limits") ?? Probe(info, "rateLimits", "rate_limits");
        if (limits is { ValueKind: JsonValueKind.Object } limitsElement)
        {
            foreach (var window in limitsElement.EnumerateObject())
            {
                if (window.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var usedPercent = ReadDouble(window.Value, "usedPercent", "used_percent");
                if (usedPercent is null)
                {
                    continue;
                }
                rateLimits.Add(new SessionRateLimitWindow(
                    DescribeWindow(window.Name, ReadDouble(window.Value, "windowMinutes", "window_minutes")),
                    usedPercent.Value,
                    ReadResetsAt(window.Value)));
            }
        }

        if (totalTokens is null && contextUsed is null && contextWindow is null && rateLimits.Count == 0)
        {
            return null;
        }
        return new SessionUsageSnapshot(totalTokens, contextWindow, contextUsed, rateLimits);
    }

    private static string DescribeWindow(string name, double? windowMinutes) =>
        windowMinutes switch
        {
            null => name,
            <= 90 => $"{Math.Round(windowMinutes.Value)}m",
            <= 1_440 => $"{Math.Round(windowMinutes.Value / 60)}h",
            <= 10_080 => "weekly",
            _ => $"{Math.Round(windowMinutes.Value / 1_440)}d"
        };

    private static DateTimeOffset? ReadResetsAt(JsonElement window)
    {
        if (Probe(window, "resetsAt", "resets_at") is { ValueKind: JsonValueKind.String } resetsAt &&
            DateTimeOffset.TryParse(resetsAt.GetString(), out var parsed))
        {
            return parsed;
        }
        var seconds = ReadDouble(window, "resetsInSeconds", "resets_in_seconds");
        return seconds is null ? null : DateTimeOffset.UtcNow.AddSeconds(seconds.Value);
    }

    private static long? ReadTokens(JsonElement? usage)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }
        var explicitTotal = ReadLong(element, "totalTokens", "total_tokens");
        if (explicitTotal is not null)
        {
            return explicitTotal;
        }
        var input = ReadLong(element, "inputTokens", "input_tokens");
        var output = ReadLong(element, "outputTokens", "output_tokens");
        if (input is null && output is null)
        {
            return null;
        }
        return (input ?? 0) + (output ?? 0);
    }

    private static JsonElement? Probe(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var found) && found.ValueKind is not JsonValueKind.Null)
            {
                return found;
            }
        }
        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] names) =>
        Probe(element, names) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static double? ReadDouble(JsonElement element, params string[] names) =>
        Probe(element, names) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var parsed)
            ? parsed
            : null;
}
