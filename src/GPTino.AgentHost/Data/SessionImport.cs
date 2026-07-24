using System.Globalization;
using System.Text;
using GPTino.AgentHost.Api;

namespace GPTino.AgentHost.Data;

/// <summary>Reserved, server-assigned message phases that mark an imported session's synthesized rows.</summary>
public static class ImportedSessionPhases
{
    /// <summary>The human-visible banner declaring every inherited id/geometry reference stale.</summary>
    public const string Banner = "imported";

    /// <summary>
    /// The model-visible deterministic replay of the prior project's transcript. The orchestrator
    /// prepends exactly this row's content to the first (thread-creating) turn's input.
    /// </summary>
    public const string ContextSeed = "imported-context";
}

/// <summary>
/// A read-only export of one archived session, produced by <see cref="ProjectArchiveReader"/> and
/// consumed by <see cref="ImportedSessionSeedBuilder"/>. Carries the newest message window plus the
/// metadata the banner needs. The archived root is never written to build this.
/// </summary>
public sealed record ArchivedSessionExport(
    string Fingerprint,
    string? ProjectName,
    string Name,
    DateTimeOffset UpdatedAt,
    int TotalMessageCount,
    IReadOnlyList<ArchivedMessage> Messages);

/// <summary>One copied transcript row to insert verbatim into the imported session.</summary>
public sealed record ImportedMessage(string Role, string Content, string? Phase, DateTimeOffset CreatedAt);

/// <summary>
/// Everything <see cref="SessionStore.ImportSessionAsync"/> writes in one transaction: the new
/// session name, the leading banner, the copied transcript rows (in order), and the trailing
/// model-visible context seed. Building this is deterministic server work (no LLM).
/// </summary>
public sealed record ImportedSessionSeed(
    string Name,
    string BannerContent,
    IReadOnlyList<ImportedMessage> Messages,
    string ContextSeedContent);

/// <summary>
/// Turns a foreign <see cref="ArchivedSessionExport"/> into an <see cref="ImportedSessionSeed"/>:
/// the "(imported)" name, the stale-reference banner, the copied rows, and a tail-truncated replay
/// block. Pure and deterministic so it is unit-testable and reproducible per the reliability-layer
/// principle (deterministic work is server-computed, not delegated to the model).
/// </summary>
public static class ImportedSessionSeedBuilder
{
    /// <summary>
    /// Hard character budget for the replay body. The imported conversation is truncated tail-first
    /// to at most this many characters so the seeded first turn never overflows the model's input.
    /// </summary>
    public const int ContextSeedCharBudget = 24_000;

    private const string SeedHeader =
        "=== Imported conversation from a previous project (a DIFFERENT Rhino/Grasshopper document; " +
        "every component id and geometry reference below is stale) ===";

    private const string SeedFooter =
        "=== End of imported conversation. Re-discover the current canvas via a snapshot before " +
        "acting on any inherited id. ===";

    public static ImportedSessionSeed Build(string fingerprint, ArchivedSessionExport export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(export);

        var name = $"{export.Name} (imported)";
        var banner = BuildBanner(fingerprint, export);
        var copied = export.Messages
            .Select(message => new ImportedMessage(message.Role, message.Content, message.Phase, message.CreatedAt))
            .ToArray();
        var contextSeed = BuildContextSeed(export.Messages);
        return new ImportedSessionSeed(name, banner, copied, contextSeed);
    }

    private static string BuildBanner(string fingerprint, ArchivedSessionExport export)
    {
        var label = string.IsNullOrWhiteSpace(export.ProjectName) ? fingerprint : export.ProjectName;
        var builder = new StringBuilder();
        builder.Append("Imported from project '").Append(label).Append("' (").Append(fingerprint)
            .Append("), session '").Append(export.Name).Append("', last active ")
            .Append(export.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(". This history was recorded against a DIFFERENT Rhino/Grasshopper document: ")
            .Append("every component id, wire, and geometry reference below is stale. Re-discover the ")
            .Append("current canvas state via a snapshot before acting on any of it.");
        if (export.TotalMessageCount > export.Messages.Count)
        {
            builder.Append(" Showing the most recent ").Append(export.Messages.Count)
                .Append(" of ").Append(export.TotalMessageCount).Append(" messages.");
        }
        return builder.ToString();
    }

    private static string BuildContextSeed(IReadOnlyList<ArchivedMessage> messages)
    {
        var conversational = messages
            .Where(message =>
                string.Equals(message.Role, "user", StringComparison.Ordinal) ||
                string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .ToList();

        // Tail-first truncation to the hard char budget: keep the newest exchanges, and when a single
        // message would overflow the remaining budget keep only its tail so the newest words survive.
        var selected = new List<string>();
        var characters = 0;
        for (var index = conversational.Count - 1; index >= 0; index--)
        {
            var remaining = ContextSeedCharBudget - characters;
            if (remaining <= 0)
            {
                break;
            }
            var line = $"{conversational[index].Role}: {conversational[index].Content}";
            if (line.Length > remaining)
            {
                line = line[^remaining..];
            }
            selected.Add(line);
            characters += line.Length;
        }
        selected.Reverse();

        var body = selected.Count == 0
            ? "(no user or assistant messages were recorded)"
            : string.Join("\n", selected);
        return $"{SeedHeader}\n{body}\n{SeedFooter}";
    }
}
