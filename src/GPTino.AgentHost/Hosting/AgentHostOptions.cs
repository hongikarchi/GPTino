using System.Security.Cryptography;
using System.Text;

namespace GPTino.AgentHost.Hosting;

public sealed class AgentHostOptions
{
    public const string SectionName = "GPTino";

    public Guid ProjectId { get; init; } = Guid.NewGuid();

    public string ProjectDirectory { get; init; } = Directory.GetCurrentDirectory();

    public string? DataDirectory { get; init; }

    public string? RhinoPath { get; init; }

    public string? GrasshopperPath { get; init; }

    public uint? RhinoDocumentSerial { get; init; }

    public string? CodexExecutable { get; init; }

    public string ApiToken { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public int MaxParallelTurns { get; init; } = 4;

    public string? BridgePipe { get; init; }

    public int? ParentProcessId { get; init; }

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            return Path.GetFullPath(DataDirectory);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var identity = string.Join(
            '\n',
            CanonicalDocumentIdentity(RhinoPath),
            CanonicalDocumentIdentity(GrasshopperPath));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(local, "GPTino", "projects", fingerprint);
    }

    internal static string? NormalizeDocumentPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    internal static string CanonicalDocumentIdentity(string? path) =>
        NormalizeDocumentPath(path)?.ToUpperInvariant() ?? string.Empty;
}

public sealed record RuntimeIdentity(
    Guid ProjectId,
    string? RhinoPath,
    string? GrasshopperPath,
    string ProjectDirectory,
    DateTimeOffset StartedAt);
