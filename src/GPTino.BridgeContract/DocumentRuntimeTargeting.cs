using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GPTino.Contracts;

namespace GPTino.BridgeContract;

public static class DocumentRuntimeTarget
{
    public static DocumentRuntime Create(
        Guid projectId,
        int rhinoProcessId,
        DateTimeOffset rhinoProcessStart,
        uint rhinoDocumentSerial,
        Guid grasshopperDocumentId,
        string rhinoPath,
        string grasshopperPath,
        long generation = 1)
    {
        var target = new DocumentRuntime(
            projectId,
            rhinoProcessId,
            rhinoProcessStart.ToUniversalTime(),
            rhinoDocumentSerial,
            grasshopperDocumentId,
            NormalizePath(rhinoPath),
            NormalizePath(grasshopperPath),
            generation);
        target.Validate();
        return target;
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

public static class DocumentRuntimeExtensions
{
    public static DocumentRuntime NextGeneration(this DocumentRuntime target)
    {
        ArgumentNullException.ThrowIfNull(target);
        checked
        {
            return target with { Generation = target.Generation + 1 };
        }
    }

    /// <summary>
    /// Stable process/document identity. Deliberately PATH-FREE: the RhinoDoc serial and Grasshopper
    /// DocumentID uniquely identify the live pair, and a Save As / rename changes the file paths without
    /// changing this key — so the AgentHost binding survives a rename in place. Generation is also excluded.
    /// </summary>
    public static string StableTargetKey(this DocumentRuntime target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var canonical = string.Join(
            "\n",
            target.ProjectId.ToString("N"),
            target.RhinoProcessId.ToString(CultureInfo.InvariantCulture),
            target.RhinoProcessStartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            target.RhinoDocumentSerial.ToString(CultureInfo.InvariantCulture),
            target.GrasshopperDocumentId.ToString("D"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static void Validate(this DocumentRuntime target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(target));
        }

        if (target.RhinoProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "RhinoProcessId must be positive.");
        }

        if (target.RhinoProcessStartedAt.UtcTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Rhino process start is required.");
        }

        if (target.RhinoDocumentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Rhino document serial must be positive.");
        }

        if (target.GrasshopperDocumentId == Guid.Empty)
        {
            throw new ArgumentException("GrasshopperDocumentId is required.", nameof(target));
        }

        ValidateAbsolutePath(target.RhinoPath, "RhinoPath");
        ValidateAbsolutePath(target.GrasshopperPath, "GrasshopperPath");

        if (target.Generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Generation must be positive.");
        }
    }

    private static void ValidateAbsolutePath(string path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{propertyName} must be a fully qualified path.", nameof(path));
        }
    }
}

public static class DocumentTargetGuard
{
    public static void RequireCurrent(DocumentRuntime expected, DocumentRuntime actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        expected.Validate();
        actual.Validate();

        if (!string.Equals(expected.StableTargetKey(), actual.StableTargetKey(), StringComparison.Ordinal) ||
            expected.Generation != actual.Generation)
        {
            throw new DocumentTargetMismatchException(expected, actual);
        }
    }
}

public sealed class DocumentTargetMismatchException : InvalidOperationException
{
    public DocumentTargetMismatchException(DocumentRuntime expected, DocumentRuntime actual)
        : base(
            $"Document target mismatch. Expected {expected.StableTargetKey()}@{expected.Generation}, " +
            $"received {actual.StableTargetKey()}@{actual.Generation}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public DocumentRuntime Expected { get; }

    public DocumentRuntime Actual { get; }
}
