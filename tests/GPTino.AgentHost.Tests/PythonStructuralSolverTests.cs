using System.Text.Json;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The SHIPPED solver asset against closed-form theory, headless: a fixed-fixed beam under
/// self-weight deflects wL⁴/384EI at midspan. Same oracle discipline as the live Karamba/PyNite
/// gates — a solver that merely "runs" proves nothing; matching mechanics does. Requires a
/// Python with PyNiteFEA on this machine (the repo's validated dev environment); a missing
/// interpreter FAILS rather than skips, because a silently-skipped theory gate reads as
/// coverage that does not exist.
/// </summary>
public sealed class PythonStructuralSolverTests
{
    private const double E = 2.1e8;           // kN/m²
    private const double IxH300 = 20400e-8;   // m⁴ (H-300x300x10x15 strong axis)
    private const double AreaH300 = 119.8e-4; // m²

    private static readonly Dictionary<string, object> Catalog = new()
    {
        ["H-300x300x10x15"] = new { H = 300.0, B = 300.0, tw = 10.0, tf = 15.0, A = 119.8, Ix = 20400.0, Iy = 6750.0 },
    };

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "assets", "data", "structural", "solver.py")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root (assets/data).");
    }

    private static string ResolveTestPython()
    {
        var overridePath = Environment.GetEnvironmentVariable("GPTINO_PYTHON");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }
        foreach (var root in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            var candidate = Path.Combine(root.Trim(), "python.exe");
            if (root.Length > 0 && File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException(
            "No python.exe on PATH and no GPTINO_PYTHON override — the solver theory gate cannot run.");
    }

    private static PythonStructuralSolver CreateSolver() => new(
        new DataLibrary(Path.Combine(RepoRoot(), "assets", "data")),
        ResolveTestPython(),
        TimeSpan.FromSeconds(120));

    [Fact]
    public async Task SolverMatchesFixedFixedBeamTheoryAndBalancesReactions()
    {
        const double lengthM = 8.0;
        var input = JsonSerializer.Serialize(new
        {
            members = new object[]
            {
                new
                {
                    mark = "SG1",
                    a = new[] { 0.0, 0.0, 0.0 },
                    b = new[] { lengthM * 1000.0, 0.0, 0.0 },
                    kind = "curve",
                    sourceObjectIds = new[] { "11111111-1111-1111-1111-111111111111" },
                },
            },
            sections = Catalog,
            markSections = new Dictionary<string, string> { ["SG1"] = "H-300x300x10x15" },
            defaultSection = "H-300x300x10x15",
            options = new { },
        });

        using var report = JsonDocument.Parse(await CreateSolver().SolveAsync(input, CancellationToken.None));
        var root = report.RootElement;

        // Both ends sit in the base band → fixed-fixed; the support rule must find exactly 2.
        Assert.Equal(2, root.GetProperty("supports").GetInt32());
        Assert.Equal(1, root.GetProperty("edgesSolved").GetInt32());

        // Closed form: δ = wL⁴ / 384EI with w = A·ρ (self weight).
        var w = AreaH300 * 78.5; // kN/m
        var theoryMm = w * Math.Pow(lengthM, 4) / (384.0 * E * IxH300) * 1000.0;
        var check = root.GetProperty("checks")[0];
        var solvedMm = check.GetProperty("deflectionMm").GetDouble();
        Assert.True(
            Math.Abs(solvedMm - theoryMm) / theoryMm < 0.02,
            $"solver {solvedMm:F4} mm vs theory {theoryMm:F4} mm");

        // Equilibrium: support reactions carry exactly the applied weight.
        Assert.True(root.GetProperty("equilibriumErrorPercent").GetDouble() < 0.5);
        // The verdict layer threads source ids through — pointing at the real solid needs them.
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            check.GetProperty("sourceObjectIds")[0].GetString());
    }

    /// <summary>
    /// A free end the user did NOT confirm and did NOT approve for repair must SURVIVE into
    /// freeEndsRemaining — silently repairing it would hide exactly the condition the ask-back
    /// exists for. With repairFreeEnds=true the same end is pulled onto the girder (T-split at
    /// 900mm, outside the 350mm snap but inside the 1500mm repair radius) and the report says so.
    /// </summary>
    [Fact]
    public async Task FreeEndsAreReportedNotRepairedUntilTheUserSaysSo()
    {
        static string BuildInput(bool repair) => JsonSerializer.Serialize(new
        {
            members = new object[]
            {
                new { mark = "SC1", a = new[] { 0.0, 0.0, 0.0 }, b = new[] { 0.0, 0.0, 3000.0 }, kind = "curve", sourceObjectIds = new[] { "aaaaaaaa-0000-0000-0000-000000000001" } },
                new { mark = "SG1", a = new[] { 0.0, 0.0, 3000.0 }, b = new[] { 6000.0, 0.0, 3000.0 }, kind = "curve", sourceObjectIds = new[] { "aaaaaaaa-0000-0000-0000-000000000002" } },
                // Secondary drawn 900mm shy of the girder axis: its near end connects to nothing
                // until the user approves repair, which lands it mid-span as a T-junction.
                new { mark = "SB1", a = new[] { 3000.0, 900.0, 3000.0 }, b = new[] { 3000.0, 4000.0, 3000.0 }, kind = "curve", sourceObjectIds = new[] { "aaaaaaaa-0000-0000-0000-000000000003" } },
            },
            sections = Catalog,
            markSections = new Dictionary<string, string>
            {
                ["SC1"] = "H-300x300x10x15",
                ["SG1"] = "H-300x300x10x15",
                ["SB1"] = "H-300x300x10x15",
            },
            defaultSection = "H-300x300x10x15",
            options = new { repairFreeEnds = repair, columnMarkPrefixes = new[] { "SC" } },
        });

        using var untouched = JsonDocument.Parse(
            await CreateSolver().SolveAsync(BuildInput(repair: false), CancellationToken.None));
        // The unconnected secondary becomes an ISLAND (dropped from the solve) — and must be
        // itemized with its source ids, never buried in a bare count: this test originally
        // expected it under freeEndsRemaining and caught the report hiding it entirely.
        var islands = untouched.RootElement.GetProperty("islandMembers").EnumerateArray().ToArray();
        Assert.Contains(islands, island =>
            island.GetProperty("sourceObjectIds").EnumerateArray()
                .Any(id => id.GetString() == "aaaaaaaa-0000-0000-0000-000000000003"));
        // The girder's far end stays a reported free end of the solved component.
        var free = untouched.RootElement.GetProperty("freeEndsRemaining").EnumerateArray().ToArray();
        Assert.True(free.Length >= 1, $"expected the girder tail free, saw {free.Length}");
        Assert.Equal(0, untouched.RootElement.GetProperty("repairedFreeEnds").GetInt32());

        using var repaired = JsonDocument.Parse(
            await CreateSolver().SolveAsync(BuildInput(repair: true), CancellationToken.None));
        Assert.True(repaired.RootElement.GetProperty("repairedFreeEnds").GetInt32() >= 1);
        Assert.Empty(repaired.RootElement.GetProperty("islandMembers").EnumerateArray());
    }
}
