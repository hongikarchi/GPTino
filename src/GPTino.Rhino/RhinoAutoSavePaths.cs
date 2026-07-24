namespace GPTino.Rhino;

/// <summary>
/// Detects paths that belong to Rhino's periodic autosave, so document observation never adopts
/// an autosave copy as the working document. Registering an autosave path would rebind the
/// document identity — and the path-pair data-directory fingerprint — to the copy, making the
/// panel report the autosave file and orphaning the real file's sessions.
/// </summary>
internal static class RhinoAutoSavePaths
{
    internal static bool IsAutoSavePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        try
        {
            var saved = Path.GetFullPath(fileName);
            var autoSaveFile = global::Rhino.ApplicationSettings.FileSettings.AutoSaveFile;
            if (!string.IsNullOrWhiteSpace(autoSaveFile))
            {
                var autoSave = Path.GetFullPath(autoSaveFile);
                if (string.Equals(saved, autoSave, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                var autoSaveDirectory = Path.GetDirectoryName(autoSave);
                if (!string.IsNullOrEmpty(autoSaveDirectory) &&
                    string.Equals(Path.GetDirectoryName(saved), autoSaveDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // The setting is known and the file lives elsewhere: this is a user-chosen path
                // and must be honored even if its NAME looks autosave-ish — Rhino pre-fills the
                // Save As dialog with the current name, so a recovered "X_RhinoAutosave.3dm"
                // legitimately saved into a real folder would otherwise be refused forever.
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Unparseable paths / unavailable settings fall through to the name heuristic below.
        }
        return Path.GetFileNameWithoutExtension(fileName)
            .EndsWith("_RhinoAutosave", StringComparison.OrdinalIgnoreCase);
    }
}
