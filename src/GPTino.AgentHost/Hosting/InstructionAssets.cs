namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Loads model-facing instruction text from markdown assets shipped beside the executable
/// (assets/instructions → instructions/), falling back to the compiled default when the file is
/// missing or unreadable. Keeping the text in data lets prompt experiments iterate with a file
/// edit instead of a plugin rebuild; the compiled fallback keeps a broken install functional.
/// </summary>
public static class InstructionAssets
{
    public static string LoadOrFallback(string fileName, string fallback)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "instructions", fileName);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the compiled default; instruction loading must never fail startup.
        }
        return fallback;
    }
}
