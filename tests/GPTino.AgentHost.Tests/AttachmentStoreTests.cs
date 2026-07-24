using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class AttachmentStoreTests
{
    private static readonly Guid SessionId = Guid.Parse("3f9a2b74-8c1d-4e5f-9a6b-1c2d3e4f5a60");

    private static string Encode(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task SaveWritesFilesUnderTheSessionDirectoryAndReturnsAbsolutePaths()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);

        var saved = await store.SaveAsync(
            SessionId,
            [
                new IncomingAttachment("plan.png", "image/png", Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47])),
                new IncomingAttachment("notes.txt", "text/plain", Encode("panel notes"))
            ]);

        Assert.Equal(2, saved.Count);
        var expectedDirectory = Path.Combine(directory.Path, "attachments", SessionId.ToString("D"));
        foreach (var attachment in saved)
        {
            Assert.True(Path.IsPathRooted(attachment.AbsolutePath));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(attachment.AbsolutePath));
            Assert.True(File.Exists(attachment.AbsolutePath));
        }
        Assert.Equal("plan.png", saved[0].FileName);
        Assert.Equal("image/png", saved[0].MediaType);
        Assert.Equal(4, saved[0].ByteCount);
        Assert.Equal("notes.txt", saved[1].FileName);
        Assert.Equal("panel notes", await File.ReadAllTextAsync(saved[1].AbsolutePath));
        Assert.EndsWith("-0-plan.png", saved[0].AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("-1-notes.txt", saved[1].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentBatchesWithTheSameFileNameNeverCollide()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);
        var first = await store.SaveAsync(
            SessionId,
            [new IncomingAttachment("shot.png", "image/png", Encode("one"))]);
        var second = await store.SaveAsync(
            SessionId,
            [new IncomingAttachment("shot.png", "image/png", Encode("two"))]);

        Assert.NotEqual(first[0].AbsolutePath, second[0].AbsolutePath);
        Assert.Equal("one", await File.ReadAllTextAsync(first[0].AbsolutePath));
        Assert.Equal("two", await File.ReadAllTextAsync(second[0].AbsolutePath));
    }

    [Fact]
    public async Task MoreThanFourAttachmentsAreRejectedBeforeAnyWrite()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);
        var attachments = Enumerable.Range(0, 5)
            .Select(index => new IncomingAttachment($"file-{index}.txt", "text/plain", Encode("x")))
            .ToList();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(SessionId, attachments));

        Assert.Contains("at most 4", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "attachments")));
    }

    [Fact]
    public async Task DecodedPayloadOverEightMebibytesIsRejectedBeforeAnyWrite()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);
        var threeMebibytes = Convert.ToBase64String(new byte[3 * 1024 * 1024]);
        var attachments = Enumerable.Range(0, 3)
            .Select(index => new IncomingAttachment($"blob-{index}.png", "image/png", threeMebibytes))
            .ToList();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(SessionId, attachments));

        Assert.Contains("8 MiB", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "attachments")));
    }

    [Fact]
    public async Task MediaTypesOutsideTheAllowlistAreRejected()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            SessionId,
            [new IncomingAttachment("payload.zip", "application/zip", Encode("zip"))]));

        Assert.Contains("unsupported media type", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "attachments")));
    }

    [Fact]
    public async Task InvalidBase64IsRejected()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            SessionId,
            [new IncomingAttachment("broken.png", "image/png", "this is *not* base64!")]));

        Assert.Contains("valid Base64", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "attachments")));
    }

    [Fact]
    public async Task HostileFileNamesCannotEscapeTheSessionDirectory()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);
        var sessionDirectory = Path.Combine(directory.Path, "attachments", SessionId.ToString("D"));

        var saved = await store.SaveAsync(
            SessionId,
            [new IncomingAttachment("..\\..\\evil.png", "image/png", Encode("payload"))]);

        var attachment = Assert.Single(saved);
        Assert.Equal("evil.png", attachment.FileName);
        Assert.Equal(sessionDirectory, Path.GetDirectoryName(attachment.AbsolutePath));
        Assert.True(File.Exists(attachment.AbsolutePath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "evil.png")));
    }

    [Fact]
    public async Task ReservedWindowsDeviceNamesAreDefused()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);

        var saved = await store.SaveAsync(
            SessionId,
            [new IncomingAttachment("CON.png", "image/png", Encode("payload"))]);

        var attachment = Assert.Single(saved);
        Assert.Equal("_CON.png", attachment.FileName);
        Assert.True(File.Exists(attachment.AbsolutePath));
    }

    [Theory]
    [InlineData("a/b/c/deep.txt", "deep.txt")]
    [InlineData("C:evil.txt", "evil.txt")]
    [InlineData("wh?at.txt", "wh_at.txt")]
    [InlineData("", "attachment")]
    [InlineData("...", "attachment")]
    public void SanitizeFileNameNeverTrustsTheClientString(string hostile, string expected)
    {
        Assert.Equal(expected, AttachmentStore.SanitizeFileName(hostile));
    }

    [Fact]
    public void SanitizeFileNameCapsLengthButKeepsTheExtension()
    {
        var sanitized = AttachmentStore.SanitizeFileName(new string('a', 200) + ".png");

        Assert.Equal(80, sanitized.Length);
        Assert.EndsWith(".png", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyAttachmentDataIsRejected()
    {
        using var directory = new TestDirectory();
        var store = new AttachmentStore(directory.Path);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            SessionId,
            [new IncomingAttachment("empty.txt", "text/plain", "")]));

        Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
    }
}
