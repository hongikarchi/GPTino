using System.Text.Json;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Tests;

public sealed class LegacyDataDirectoryAdoptionTests
{
    [Fact]
    public void AdoptsMatchingRootWithNewestRuntimeDatabase()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        var older = CreateLegacyRoot(
            parent,
            "AAAAAAAAAAAAAAA1",
            rhino,
            "older-database",
            DateTime.UtcNow.AddDays(-3));
        var newest = CreateLegacyRoot(
            parent,
            "BBBBBBBBBBBBBBB2",
            // The manifest records the path with different case; matching is canonical.
            rhino.ToLowerInvariant(),
            "newest-database",
            DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(newest, "runtime.db-wal"), "newest-wal");
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);

        Assert.True(LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null));

        Assert.Equal("newest-database", File.ReadAllText(Path.Combine(newRoot, "runtime.db")));
        Assert.Equal("newest-wal", File.ReadAllText(Path.Combine(newRoot, "runtime.db-wal")));
        Assert.Equal(newest, File.ReadAllText(Path.Combine(newRoot, "adopted-from.txt")));
        Assert.True(File.Exists(Path.Combine(newRoot, "context", "project.json")));
        Assert.Equal(
            "rules BBBBBBBBBBBBBBB2",
            File.ReadAllText(Path.Combine(newRoot, "context", "rules.md")));
        Assert.Equal(
            "attachment BBBBBBBBBBBBBBB2",
            File.ReadAllText(Path.Combine(newRoot, "attachments", "nested", "a.bin")));
        // Per-session artifact drafts are durable session data and travel with runtime.db.
        Assert.Equal(
            "artifact BBBBBBBBBBBBBBB2",
            File.ReadAllText(Path.Combine(newRoot, "artifacts", "session", "draft.py")));
        // Runtime-scoped state never travels between roots.
        Assert.False(File.Exists(Path.Combine(newRoot, "live-jobs.db")));
        Assert.False(File.Exists(Path.Combine(newRoot, ".gptino-instance.lock")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "history")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "workspace")));
        // No staged temp files or directories linger.
        Assert.Empty(Directory.GetFiles(newRoot, "*.adopting"));
        Assert.Empty(Directory.GetDirectories(newRoot, "*.adopting"));
        // The older sibling was left untouched.
        Assert.Equal("older-database", File.ReadAllText(Path.Combine(older, "runtime.db")));
    }

    [Fact]
    public void SkipsWhenRuntimeDatabaseAlreadyExists()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        CreateLegacyRoot(parent, "AAAAAAAAAAAAAAA1", rhino, "legacy-database", DateTime.UtcNow.AddDays(-1));
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);
        File.WriteAllText(Path.Combine(newRoot, "runtime.db"), "existing-database");

        Assert.False(LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null));

        Assert.Equal("existing-database", File.ReadAllText(Path.Combine(newRoot, "runtime.db")));
        Assert.False(File.Exists(Path.Combine(newRoot, "adopted-from.txt")));
    }

    [Fact]
    public void IgnoresNonMatchingRhinoFile()
    {
        using var directory = new TestDirectory();
        var parent = directory.GetPath("projects");
        CreateLegacyRoot(
            parent,
            "AAAAAAAAAAAAAAA1",
            directory.GetPath("Other.3dm"),
            "other-database",
            DateTime.UtcNow.AddDays(-1));
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);

        Assert.False(LegacyDataDirectoryAdoption.TryAdopt(
            newRoot,
            directory.GetPath("Model.3dm"),
            parent,
            logger: null));

        Assert.False(File.Exists(Path.Combine(newRoot, "runtime.db")));
        Assert.False(File.Exists(Path.Combine(newRoot, "adopted-from.txt")));
    }

    [Fact]
    public void SurvivesGarbageProjectJson()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        var garbage = CreateLegacyRoot(
            parent,
            "AAAAAAAAAAAAAAA1",
            rhino,
            "garbage-database",
            DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(garbage, "context", "project.json"), "{ not json !!");
        var valid = CreateLegacyRoot(
            parent,
            "BBBBBBBBBBBBBBB2",
            rhino,
            "valid-database",
            DateTime.UtcNow.AddDays(-2));
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);

        // The unreadable manifest disqualifies only its own root, even though its
        // runtime.db is newer than the valid candidate's.
        Assert.True(LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null));

        Assert.Equal("valid-database", File.ReadAllText(Path.Combine(newRoot, "runtime.db")));
        Assert.Equal(valid, File.ReadAllText(Path.Combine(newRoot, "adopted-from.txt")));
    }

    [Fact]
    public void SkipsEntirelyWhenTheDataDirectoryIsExplicit()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        CreateLegacyRoot(parent, "AAAAAAAAAAAAAAA1", rhino, "legacy-database", DateTime.UtcNow.AddDays(-1));
        var explicitRoot = directory.GetPath("dev-sandbox");
        Directory.CreateDirectory(explicitRoot);
        var options = new AgentHostOptions
        {
            DataDirectory = explicitRoot,
            RhinoPath = rhino
        };

        // Dev-mode/benchmark runs pass an explicit --data-directory; importing a matching
        // production root into that sandbox would break exactly the isolation they exist for.
        Assert.False(LegacyDataDirectoryAdoption.TryAdopt(options, parent, logger: null));

        Assert.False(File.Exists(Path.Combine(explicitRoot, "runtime.db")));
        Assert.False(Directory.Exists(Path.Combine(explicitRoot, "context")));
        Assert.False(File.Exists(Path.Combine(explicitRoot, "adopted-from.txt")));
    }

    [Fact]
    public void ContextCopyFailureLeavesTheRootRetryableAndALaterRetrySucceeds()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        var legacy = CreateLegacyRoot(
            parent,
            "AAAAAAAAAAAAAAA1",
            rhino,
            "legacy-database",
            DateTime.UtcNow.AddDays(-1));
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);

        // Hold a context file open with FileShare.None so the staging copy fails mid-adoption.
        using (new FileStream(
            Path.Combine(legacy, "context", "rules.md"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            Assert.False(LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null));
        }

        // The failed attempt left the new root runtime.db-less and empty of partial payloads, so
        // the one-time gate does not seal it and the next launch retries.
        Assert.False(File.Exists(Path.Combine(newRoot, "runtime.db")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "context")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "attachments")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "artifacts")));
        Assert.False(File.Exists(Path.Combine(newRoot, "adopted-from.txt")));
        Assert.Empty(Directory.GetFiles(newRoot, "*.adopting"));
        Assert.Empty(Directory.GetDirectories(newRoot, "*.adopting"));

        Assert.True(LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null));

        Assert.Equal("legacy-database", File.ReadAllText(Path.Combine(newRoot, "runtime.db")));
        Assert.Equal(
            "rules AAAAAAAAAAAAAAA1",
            File.ReadAllText(Path.Combine(newRoot, "context", "rules.md")));
        Assert.Equal(legacy, File.ReadAllText(Path.Combine(newRoot, "adopted-from.txt")));
    }

    [Fact]
    public void FailedNewestCandidateFallsBackToOlderOneWithoutMixingData()
    {
        using var directory = new TestDirectory();
        var rhino = directory.GetPath("Model.3dm");
        var parent = directory.GetPath("projects");
        var older = CreateLegacyRoot(
            parent,
            "AAAAAAAAAAAAAAA1",
            rhino,
            "older-database",
            DateTime.UtcNow.AddDays(-3));
        var newest = CreateLegacyRoot(
            parent,
            "BBBBBBBBBBBBBBB2",
            rhino,
            "newest-database",
            DateTime.UtcNow.AddDays(-1));
        var newRoot = Path.Combine(parent, "1111111111111111");
        Directory.CreateDirectory(newRoot);

        // The newest candidate fails mid-copy; per-candidate cleanup must hand the older
        // candidate a clean root so the adopted state is entirely one candidate's.
        bool adopted;
        using (new FileStream(
            Path.Combine(newest, "context", "rules.md"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            adopted = LegacyDataDirectoryAdoption.TryAdopt(newRoot, rhino, parent, logger: null);
        }

        Assert.True(adopted);
        Assert.Equal("older-database", File.ReadAllText(Path.Combine(newRoot, "runtime.db")));
        Assert.Equal(
            "rules AAAAAAAAAAAAAAA1",
            File.ReadAllText(Path.Combine(newRoot, "context", "rules.md")));
        Assert.Equal(
            "attachment AAAAAAAAAAAAAAA1",
            File.ReadAllText(Path.Combine(newRoot, "attachments", "nested", "a.bin")));
        Assert.Equal(
            "artifact AAAAAAAAAAAAAAA1",
            File.ReadAllText(Path.Combine(newRoot, "artifacts", "session", "draft.py")));
        Assert.Equal(older, File.ReadAllText(Path.Combine(newRoot, "adopted-from.txt")));
        Assert.Empty(Directory.GetFiles(newRoot, "*.adopting"));
        Assert.Empty(Directory.GetDirectories(newRoot, "*.adopting"));
    }

    private static string CreateLegacyRoot(
        string projectsParent,
        string fingerprint,
        string rhinoFile,
        string databaseContent,
        DateTime databaseLastWriteUtc)
    {
        var root = Path.Combine(projectsParent, fingerprint);
        Directory.CreateDirectory(Path.Combine(root, "context"));
        File.WriteAllText(
            Path.Combine(root, "context", "project.json"),
            JsonSerializer.Serialize(new { schema = "gptino-context-v1", rhinoFile }));
        File.WriteAllText(Path.Combine(root, "context", "rules.md"), $"rules {fingerprint}");
        Directory.CreateDirectory(Path.Combine(root, "attachments", "nested"));
        File.WriteAllText(
            Path.Combine(root, "attachments", "nested", "a.bin"),
            $"attachment {fingerprint}");
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "session"));
        File.WriteAllText(
            Path.Combine(root, "artifacts", "session", "draft.py"),
            $"artifact {fingerprint}");
        var databasePath = Path.Combine(root, "runtime.db");
        File.WriteAllText(databasePath, databaseContent);
        File.SetLastWriteTimeUtc(databasePath, databaseLastWriteUtc);
        // Runtime-scoped state that adoption must leave behind.
        File.WriteAllText(Path.Combine(root, "live-jobs.db"), "live-jobs");
        File.WriteAllText(Path.Combine(root, ".gptino-instance.lock"), "pid=0");
        Directory.CreateDirectory(Path.Combine(root, "history"));
        File.WriteAllText(Path.Combine(root, "history", "entry.txt"), "history");
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        File.WriteAllText(Path.Combine(root, "workspace", "scratch.txt"), "workspace");
        return root;
    }
}
