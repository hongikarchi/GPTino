using System.Security.Cryptography;

namespace GPTino.AgentHost.Hosting;

internal static class AgentHostArguments
{
    public static AgentHostOptions Parse(string[] args, IConfiguration configuration)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            var key = args[index][2..];
            values[key] = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
        }

        var projectDirectory = Value("project-directory")
            ?? configuration[$"{AgentHostOptions.SectionName}:ProjectDirectory"]
            ?? Directory.GetCurrentDirectory();
        var rhinoPath = AgentHostOptions.NormalizeDocumentPath(
            Value("rhino") ?? configuration[$"{AgentHostOptions.SectionName}:RhinoPath"]);
        var grasshopperPath = AgentHostOptions.NormalizeDocumentPath(
            Value("grasshopper") ?? configuration[$"{AgentHostOptions.SectionName}:GrasshopperPath"]);
        var projectIdText = Value("project-id") ?? configuration[$"{AgentHostOptions.SectionName}:ProjectId"];
        // ProjectId is a runtime-tuple identity minted by the plugin (GptinoRuntimeHost.CreateProjectId) and
        // always supplied via --project-id on a bridge launch. It is NOT derived from file paths any more, so
        // there is no path-based fallback to reconstruct here: a standalone/config launch without --project-id
        // just gets a fresh id (it has no plugin peer to agree with). A path hash here would only ever fail the
        // plugin's project_mismatch check.
        var projectId = Guid.TryParse(projectIdText, out var parsedProjectId)
            ? parsedProjectId
            : Guid.NewGuid();
        var parentText = Value("parent-process-id");
        var rhinoDocumentSerialText = Value("rhino-document-serial")
            ?? configuration[$"{AgentHostOptions.SectionName}:RhinoDocumentSerial"];
        var maxTurnsText = Value("max-parallel-turns") ?? configuration[$"{AgentHostOptions.SectionName}:MaxParallelTurns"];

        return new AgentHostOptions
        {
            ProjectId = projectId,
            ProjectDirectory = Path.GetFullPath(projectDirectory),
            DataDirectory = Value("data-directory") ?? configuration[$"{AgentHostOptions.SectionName}:DataDirectory"],
            RhinoPath = rhinoPath,
            GrasshopperPath = grasshopperPath,
            RhinoDocumentSerial = uint.TryParse(rhinoDocumentSerialText, out var rhinoDocumentSerial) &&
                rhinoDocumentSerial != 0
                    ? rhinoDocumentSerial
                    : null,
            CodexExecutable = Value("codex-executable")
                ?? configuration[$"{AgentHostOptions.SectionName}:CodexExecutable"],
            ApiToken = Environment.GetEnvironmentVariable("GPTINO_API_TOKEN")
                ?? configuration[$"{AgentHostOptions.SectionName}:ApiToken"]
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            MaxParallelTurns = int.TryParse(maxTurnsText, out var maxTurns) ? Math.Clamp(maxTurns, 1, 16) : 4,
            BridgePipe = Value("bridge-pipe") ?? Environment.GetEnvironmentVariable("GPTINO_BRIDGE_PIPE"),
            ParentProcessId = int.TryParse(parentText, out var parentId) ? parentId : null
        };

        string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
    }
}
