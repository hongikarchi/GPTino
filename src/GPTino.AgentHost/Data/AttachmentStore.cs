using System.Globalization;
using GPTino.AgentHost.Api;

namespace GPTino.AgentHost.Data;

/// <summary>
/// Persists user-supplied message attachments under the project data root so the codex agent can
/// read them from disk. Every rule is preflighted across the whole batch before the first byte is
/// written; violations throw <see cref="ArgumentException"/>, which the API middleware maps to a
/// 400 invalid_request response.
/// </summary>
public sealed class AttachmentStore(string dataRoot)
{
    public const int MaxAttachmentsPerMessage = 4;

    /// <summary>Maximum decoded payload across all attachments of one message: 8 MiB.</summary>
    public const long MaxTotalDecodedBytes = 8 * 1024 * 1024;

    private const int MaxFileNameLength = 80;

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif",
        "text/plain",
        "text/markdown",
        "application/json",
        "text/csv",
        "application/pdf"
    };

    // Windows device names are reserved regardless of extension on legacy paths; never let a
    // client-controlled base name resolve to one.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _dataRoot = dataRoot;

    public async Task<IReadOnlyList<SavedAttachment>> SaveAsync(
        Guid sessionId,
        IReadOnlyList<IncomingAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return [];
        }
        if (attachments.Count > MaxAttachmentsPerMessage)
        {
            throw new ArgumentException(
                $"A message can carry at most {MaxAttachmentsPerMessage} attachments; {attachments.Count} were sent.");
        }

        // Preflight: decode and validate every attachment before any write, so a rejected batch
        // leaves no partial files behind.
        var staged = new (string FileName, string MediaType, byte[] Bytes)[attachments.Count];
        long totalBytes = 0;
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            var mediaType = attachment.MediaType?.Trim() ?? string.Empty;
            if (!AllowedMediaTypes.Contains(mediaType))
            {
                throw new ArgumentException(
                    $"Attachment '{attachment.FileName}' has unsupported media type '{attachment.MediaType}'.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(attachment.DataBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new ArgumentException(
                    $"Attachment '{attachment.FileName}' does not contain valid Base64 data.");
            }
            if (bytes.Length == 0)
            {
                throw new ArgumentException($"Attachment '{attachment.FileName}' is empty.");
            }

            totalBytes += bytes.Length;
            if (totalBytes > MaxTotalDecodedBytes)
            {
                throw new ArgumentException(
                    $"Attachments exceed the {MaxTotalDecodedBytes / (1024 * 1024)} MiB limit per message.");
            }

            staged[index] = (SanitizeFileName(attachment.FileName), mediaType, bytes);
        }

        var directory = Path.Combine(_dataRoot, "attachments", sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        // A per-batch random fragment keeps concurrent messages (same millisecond, same file
        // name) from silently overwriting each other's stored bytes.
        var batchId = Guid.NewGuid().ToString("N")[..8];

        var saved = new SavedAttachment[staged.Length];
        for (var index = 0; index < staged.Length; index++)
        {
            var (fileName, mediaType, bytes) = staged[index];
            var storedName = $"{timestamp}-{batchId}-{index}-{fileName}";
            var absolutePath = Path.GetFullPath(Path.Combine(directory, storedName));
            await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken).ConfigureAwait(false);
            saved[index] = new SavedAttachment(fileName, mediaType, absolutePath, bytes.Length);
        }
        return saved;
    }

    /// <summary>
    /// Reduces a client-controlled name to a safe file name: directory parts stripped, invalid
    /// characters replaced, reserved device names defused, length capped. The client string is
    /// never trusted as a path.
    /// </summary>
    internal static string SanitizeFileName(string? fileName)
    {
        var name = fileName ?? string.Empty;
        var cut = name.LastIndexOfAny(['\\', '/', ':']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 || char.IsControl(character) ? '_' : character);
        }
        name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length == 0 || name.All(character => character == '.' || character == '_'))
        {
            name = "attachment";
        }
        var baseName = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(baseName))
        {
            name = $"_{name}";
        }
        if (name.Length > MaxFileNameLength)
        {
            // Keep the extension recognizable when capping.
            var extension = Path.GetExtension(name);
            if (extension.Length is > 0 and < 12)
            {
                name = string.Concat(name.AsSpan(0, MaxFileNameLength - extension.Length), extension);
            }
            else
            {
                name = name[..MaxFileNameLength];
            }
        }
        return name;
    }
}

/// <summary>One attachment persisted on disk for a message, referenced by absolute path.</summary>
public sealed record SavedAttachment(
    string FileName,
    string MediaType,
    string AbsolutePath,
    long ByteCount);
