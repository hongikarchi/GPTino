using System.Globalization;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class SessionImportTests
{
    [Fact]
    public async Task ImportSessionCreatesBannerCopiedRowsAndSeedWithNullThreadAndDoc()
    {
        using var directory = new TestDirectory();
        var store = new SessionStore(directory.GetPath("runtime.db"));
        await store.InitializeAsync();
        // A pre-existing session so MAX(sort_order)+1 is exercised and left untouched.
        var existing = await store.CreateSessionAsync(new CreateSessionRequest("Existing"));

        var firstAt = DateTimeOffset.Parse("2026-06-01T10:00:00+00:00", CultureInfo.InvariantCulture);
        var secondAt = firstAt.AddMinutes(1);
        var seed = new ImportedSessionSeed(
            "Facade (imported)",
            "Imported from project 'Tower' (00AA11BB22CC33DD). Every id below is STALE.",
            [
                new ImportedMessage("user", "prior request", "request", firstAt),
                new ImportedMessage("assistant", "prior answer", "final_answer", secondAt),
            ],
            "=== Imported conversation ===\nuser: prior request\nassistant: prior answer\n=== End ===");

        var imported = await store.ImportSessionAsync(seed);

        Assert.Equal("Facade (imported)", imported.Name);
        Assert.Equal("modeler", imported.Role);
        Assert.Equal("auto", imported.ModelProfile);
        Assert.Equal(SessionStates.Idle, imported.State);
        Assert.Null(imported.CodexThreadId);
        Assert.Null(imported.GrasshopperDoc);
        Assert.Equal(existing.Order + 1, imported.Order);

        // Rowid/insertion order drives display order: banner, copied rows (verbatim, older createdAt
        // preserved), then the trailing context seed.
        var messages = await store.ReadMessagesAsync(imported.Id);
        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("system", message.Role);
                Assert.Equal(ImportedSessionPhases.Banner, message.Phase);
                Assert.Contains("STALE", message.Content, StringComparison.Ordinal);
            },
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("prior request", message.Content);
                Assert.Equal("request", message.Phase);
                Assert.Equal(firstAt, message.CreatedAt);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("prior answer", message.Content);
                Assert.Equal("final_answer", message.Phase);
                Assert.Equal(secondAt, message.CreatedAt);
            },
            message =>
            {
                Assert.Equal("system", message.Role);
                Assert.Equal(ImportedSessionPhases.ContextSeed, message.Phase);
                Assert.Equal(seed.ContextSeedContent, message.Content);
            });

        // The orchestrator reads exactly the seed row's content on the new-thread branch.
        Assert.Equal(seed.ContextSeedContent, await store.ReadImportedContextAsync(imported.Id));
        // Copied rows carry no client_message_id, so a later real turn can reuse any id freely.
        Assert.Null(await store.ReadImportedContextAsync(existing.Id));
        // The pre-existing session is untouched.
        Assert.Empty(await store.ReadMessagesAsync(existing.Id));
    }

    [Fact]
    public void SeedBuilderTailTruncatesTheReplayToTheCharBudget()
    {
        var messages = new List<ArchivedMessage>();
        // Oldest first; each ~1000 chars, so the total far exceeds the 24k budget.
        for (var index = 0; index < 60; index++)
        {
            messages.Add(new ArchivedMessage(
                index + 1,
                index % 2 == 0 ? "user" : "assistant",
                $"MSG{index:D2}-" + new string('x', 1000),
                null,
                DateTimeOffset.UtcNow.AddMinutes(index)));
        }
        var export = new ArchivedSessionExport(
            "00AA11BB22CC33DD",
            "Tower",
            "Facade",
            DateTimeOffset.UtcNow,
            5000,
            messages);

        var seed = ImportedSessionSeedBuilder.Build(export.Fingerprint, export);

        Assert.Equal("Facade (imported)", seed.Name);
        // Tail-first: the newest survives, the oldest is dropped.
        Assert.Contains("MSG59-", seed.ContextSeedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("MSG00-", seed.ContextSeedContent, StringComparison.Ordinal);
        // The replay honors the hard budget (a small fixed header/footer is the only overhead).
        Assert.True(
            seed.ContextSeedContent.Length <= ImportedSessionSeedBuilder.ContextSeedCharBudget + 512,
            $"seed length {seed.ContextSeedContent.Length} exceeded the budget");
        // Copied rows carry the full window; the banner discloses the older history left behind.
        Assert.Equal(60, seed.Messages.Count);
        Assert.Contains("of 5000 messages", seed.BannerContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SeedBuilderSkipsSystemRowsAndSurvivesAnEmptyConversation()
    {
        var export = new ArchivedSessionExport(
            "00AA11BB22CC33DD",
            null,
            "Blank",
            DateTimeOffset.UtcNow,
            1,
            [new ArchivedMessage(1, "system", "recovery notice", "recovery", DateTimeOffset.UtcNow)]);

        var seed = ImportedSessionSeedBuilder.Build(export.Fingerprint, export);

        // No user/assistant rows means no replay body, but the banner and structure still hold, and
        // with no project name the fingerprint is the label.
        Assert.DoesNotContain("recovery notice", seed.ContextSeedContent, StringComparison.Ordinal);
        Assert.Contains("no user or assistant messages", seed.ContextSeedContent, StringComparison.Ordinal);
        Assert.Contains("00AA11BB22CC33DD", seed.BannerContent, StringComparison.Ordinal);
        // Full window shown (1 of 1), so the banner omits the truncation disclosure.
        Assert.DoesNotContain("Showing the most recent", seed.BannerContent, StringComparison.Ordinal);
    }
}
