using System.Text;
using GPTino.History;

namespace GPTino.History.Tests;

public sealed class ManagedHistoryRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FindRepositoryRoot(),
        "artifacts",
        "test-temp",
        "history",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Baseline_and_verified_change_advance_history()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("manifest.json", "{\"revision\":0}")), Guid.NewGuid());

        var metadata = Metadata(1);
        var next = await repository.CommitAsync(new HistoryCommitRequest(
            baseline.Head,
            Files(("manifest.json", "{\"revision\":1}"), ("grasshopper/definition.ghx", "<Archive />")),
            metadata));

        Assert.True(next.CreatedCommit);
        Assert.NotEqual(baseline.Head, next.Head);
        Assert.Equal(next.Head, repository.ReadHead());
        Assert.True(repository.Verify().IsValid);
    }

    [Fact]
    public async Task Commit_rejects_stale_expected_head()
    {
        var repository = new ManagedHistoryRepository(_root);
        await repository.InitializeBaselineAsync(Files(("manifest.json", "{}")), Guid.NewGuid());

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => repository.CommitAsync(
            new HistoryCommitRequest("stale", Files(("manifest.json", "{\"x\":1}")), Metadata(1))));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(".git")]
    [InlineData(".git.")]
    [InlineData("nested/.git ")]
    [InlineData(".git/config")]
    [InlineData("nested/.GIT/config")]
    [InlineData("safe/../secret.txt")]
    public async Task Baseline_rejects_unsafe_paths(string path)
    {
        var repository = new ManagedHistoryRepository(_root);
        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files((path, "no")), Guid.NewGuid()));
    }

    [Fact]
    public async Task Baseline_validates_entire_file_set_before_initializing_or_writing()
    {
        var repository = new ManagedHistoryRepository(_root);

        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files(("safe.txt", "must not be written"), ("nested/.git/config", "unsafe")),
            Guid.NewGuid()));

        Assert.False(repository.IsInitialized);
        Assert.False(File.Exists(Path.Combine(_root, "safe.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task Commit_validates_entire_file_set_before_mutating_worktree()
    {
        var repository = new ManagedHistoryRepository(_root);
        var baseline = await repository.InitializeBaselineAsync(
            Files(("safe.txt", "before")), Guid.NewGuid());

        await Assert.ThrowsAsync<HistoryPathException>(() => repository.CommitAsync(
            new HistoryCommitRequest(
                baseline.Head,
                Files(("safe.txt", "after"), (".git/config", "unsafe")),
                Metadata(1))));

        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(_root, "safe.txt")));
        Assert.Equal(baseline.Head, repository.ReadHead());
        Assert.True(repository.Verify().IsValid);
    }

    [Fact]
    public async Task Baseline_rejects_existing_reparse_point_before_writing()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "actual");
        var link = Path.Combine(_root, "linked");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var repository = new ManagedHistoryRepository(_root);
        await Assert.ThrowsAsync<HistoryPathException>(() => repository.InitializeBaselineAsync(
            Files(("linked/unsafe.txt", "no")), Guid.NewGuid()));

        Assert.False(repository.IsInitialized);
        Assert.False(File.Exists(Path.Combine(target, "unsafe.txt")));
    }

    [Fact]
    public void Pair_fingerprint_is_case_insensitive_on_windows()
    {
        var lower = ProjectHomeLayout.StablePairFingerprint("c:/models/a.3dm", "c:/models/a.gh");
        var upper = ProjectHomeLayout.StablePairFingerprint("C:/MODELS/A.3DM", "C:/MODELS/A.GH");
        Assert.Equal(lower, upper);
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(
            item => item.Path,
            item => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(item.Text),
            StringComparer.Ordinal);

    private static HistoryCommitMetadata Metadata(int revision) =>
        new(revision, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"r{revision}", "sha256:change", "Standard", "test change");

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "GPTino.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the GPTino repository root for test artifacts.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            DeleteTestTree(_root);
        }
    }

    private static void DeleteTestTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTestTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        File.SetAttributes(directory, FileAttributes.Normal);
        Directory.Delete(directory);
    }
}
