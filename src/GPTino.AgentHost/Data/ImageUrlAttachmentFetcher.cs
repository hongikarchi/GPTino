using System.Net;
using System.Text.RegularExpressions;
using GPTino.AgentHost.Api;
using Microsoft.Extensions.Logging;

namespace GPTino.AgentHost.Data;

/// <summary>
/// Turns image URLs pasted into a chat message into local attachments. The AgentHost is NOT the
/// sandboxed Codex process, so it can reach the network: it downloads the referenced images here
/// and hands them to <see cref="AttachmentStore"/>, after which they ride the same guaranteed
/// localImage input channel as a file the user attached with the paperclip. This makes "paste an
/// image link" deterministic instead of depending on whether Codex decides to shell out and fetch
/// it (which the base instructions actively discourage).
/// </summary>
public sealed class ImageUrlAttachmentFetcher
{
    private const int MaxUrlsPerMessage = AttachmentStore.MaxAttachmentsPerMessage;
    private const long MaxTotalBytes = AttachmentStore.MaxTotalDecodedBytes;

    // Only URLs that visibly name an image file are fetched — we never speculatively download every
    // link a user pastes. An optional query string (CDN cache tokens, sizing params) is tolerated.
    private static readonly Regex ImageUrlPattern = new(
        @"https?://[^\s<>""')\]]+\.(?:png|jpe?g|webp|gif)(?:\?[^\s<>""')\]]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private readonly AttachmentStore _attachments;
    private readonly ILogger<ImageUrlAttachmentFetcher>? _logger;

    public ImageUrlAttachmentFetcher(AttachmentStore attachments, ILogger<ImageUrlAttachmentFetcher>? logger = null)
    {
        _attachments = attachments;
        _logger = logger;
    }

    /// <summary>
    /// Extracts the distinct, fetchable image URLs from a message (http/https only, private and
    /// loopback hosts rejected, capped at the per-message attachment limit). Pure and deterministic
    /// so it can be unit-tested without touching the network.
    /// </summary>
    public static IReadOnlyList<string> ExtractImageUrls(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<string>();
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();
        foreach (Match match in ImageUrlPattern.Matches(content))
        {
            var url = match.Value;
            if (IsFetchableHttpUrl(url) && seen.Add(url))
            {
                urls.Add(url);
                if (urls.Count >= MaxUrlsPerMessage)
                {
                    break;
                }
            }
        }
        return urls;
    }

    /// <summary>
    /// Best-effort: downloads the image URLs found in <paramref name="content"/> and persists them
    /// as attachments for this session. A URL that fails to fetch, is not an accepted image type, or
    /// would exceed the size budget is skipped silently — a bad link never fails the user's turn.
    /// </summary>
    public async Task<IReadOnlyList<SavedAttachment>> FetchAsync(
        Guid sessionId,
        string? content,
        CancellationToken cancellationToken)
    {
        var urls = ExtractImageUrls(content);
        if (urls.Count == 0)
        {
            return Array.Empty<SavedAttachment>();
        }
        var incoming = new List<IncomingAttachment>();
        long total = 0;
        foreach (var url in urls)
        {
            try
            {
                using var response = await Http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var mediaType = ResolveImageMediaType(response.Content.Headers.ContentType?.MediaType, url);
                if (mediaType is null)
                {
                    continue;
                }
                if (response.Content.Headers.ContentLength is > MaxTotalBytes)
                {
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0 || total + bytes.Length > MaxTotalBytes)
                {
                    continue;
                }
                total += bytes.Length;
                incoming.Add(new IncomingAttachment(FileNameFor(url, mediaType), mediaType, Convert.ToBase64String(bytes)));
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Skipped unfetchable image URL {Url}", url);
            }
        }
        if (incoming.Count == 0)
        {
            return Array.Empty<SavedAttachment>();
        }
        try
        {
            return await _attachments.SaveAsync(sessionId, incoming, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Persisting fetched URL images failed; continuing without them.");
            return Array.Empty<SavedAttachment>();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxTotalBytes + 1,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GPTino/0.1 (+image-attachment-fetch)");
        return client;
    }

    private static string? ResolveImageMediaType(string? headerMediaType, string url)
    {
        var normalized = headerMediaType?.Trim().ToLowerInvariant();
        if (normalized is "image/png" or "image/jpeg" or "image/webp" or "image/gif")
        {
            return normalized;
        }
        // Fall back to the URL extension when the server sent no / a generic content type.
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
    }

    private static string FileNameFor(string url, string mediaType)
    {
        var candidate = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var extension = mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".img",
            };
            candidate = $"linked-image{extension}";
        }
        return candidate;
    }

    private static bool IsFetchableHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // SSRF guard: never let a pasted link point the host at loopback, link-local (incl. the
        // cloud metadata endpoint), or RFC1918 private space. This is a host-literal check, not a
        // DNS resolution — adequate for user-pasted public image links on a desktop app.
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return false;
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var octets = ip.GetAddressBytes();
                if (octets[0] == 10 ||
                    (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
                    (octets[0] == 192 && octets[1] == 168) ||
                    (octets[0] == 169 && octets[1] == 254))
                {
                    return false;
                }
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
            {
                return false;
            }
        }
        return true;
    }
}
