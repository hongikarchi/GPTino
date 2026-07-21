using System.Diagnostics;

namespace GPTino.LiveE2E;

internal static class RhinoLaunchArguments
{
    public static void Configure(
        ProcessStartInfo startInfo,
        string grasshopperDocumentPath,
        string rhinoDocumentPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.ArgumentList.Count != 0 || !string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new InvalidOperationException("Rhino launch arguments must be configured exactly once.");
        }

        startInfo.Arguments = Build(grasshopperDocumentPath, rhinoDocumentPath);
    }

    internal static string Build(string grasshopperDocumentPath, string rhinoDocumentPath)
    {
        ValidatePath(grasshopperDocumentPath, "Grasshopper document");
        ValidatePath(rhinoDocumentPath, "Rhino document");

        var macro =
            "_GPTinoOpenPanel " +
            "_-Grasshopper _Banner _Disable _Enter " +
            $"_-Grasshopper _Document _Open \"\"{grasshopperDocumentPath}\"\" _Enter " +
            "_GPTinoOpenPanel";

        // Rhino parses /runscript from the raw command line rather than ordinary argv.
        // The macro therefore needs one outer quote pair, while a path inside that
        // macro needs doubled quotes.
        return $"/netcore-8 /nosplash /runscript=\"{macro}\" \"{rhinoDocumentPath}\"";
    }

    private static void ValidatePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{label} path is required.", nameof(path));
        }
        if (path.IndexOfAny(['\"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"{label} path contains an unsafe command-line character.", nameof(path));
        }
    }
}
