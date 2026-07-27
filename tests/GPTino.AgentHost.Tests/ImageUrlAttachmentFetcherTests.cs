using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class ImageUrlAttachmentFetcherTests
{
    [Fact]
    public void ExtractsDistinctImageUrlsAcrossFormatsAndToleratesQueryStrings()
    {
        var content = """
            Use these refs:
            https://cdn.example.com/a.png
            http://img.example.org/b.JPG?width=800&token=abc
            see also https://example.net/pics/c.webp and https://example.net/pics/d.gif
            duplicate: https://cdn.example.com/a.png
            """;

        var urls = ImageUrlAttachmentFetcher.ExtractImageUrls(content);

        Assert.Equal(
            new[]
            {
                "https://cdn.example.com/a.png",
                "http://img.example.org/b.JPG?width=800&token=abc",
                "https://example.net/pics/c.webp",
                "https://example.net/pics/d.gif",
            },
            urls);
    }

    [Fact]
    public void IgnoresNonImageLinksPlainTextAndUnsupportedSchemes()
    {
        var content = "docs at https://example.com/page and ftp://host/x.png and a bare word file.png";
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(content));
    }

    [Fact]
    public void RejectsLoopbackAndPrivateAndLinkLocalHosts()
    {
        var content = """
            http://localhost/a.png
            http://127.0.0.1/b.png
            http://10.0.0.5/c.png
            http://192.168.1.9/d.png
            http://172.16.4.4/e.png
            http://169.254.169.254/latest/meta-data/f.png
            """;
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(content));
    }

    [Fact]
    public void CapsAtThePerMessageAttachmentLimit()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(0, 10).Select(index => $"https://cdn.example.com/img-{index}.png"));
        Assert.Equal(AttachmentStore.MaxAttachmentsPerMessage, ImageUrlAttachmentFetcher.ExtractImageUrls(content).Count);
    }

    [Fact]
    public void ReturnsEmptyForNullOrBlankContent()
    {
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(null));
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls("   "));
    }
}
