using System.Text;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Assembles the layered thread instructions every Codex thread receives:
/// static base contract → plugin house rules (identical for every project, machine,
/// and user) → built-in skills index → per-project rules and memory. Layered so the
/// plugin's conventions travel with the install while projects stay customizable.
/// </summary>
public sealed class InstructionAssembler : IThreadInstructionComposer
{
    private readonly ProjectContextStore _projectContext;
    private readonly SkillLibrary _skills;

    public InstructionAssembler(ProjectContextStore projectContext, SkillLibrary skills)
    {
        _projectContext = projectContext;
        _skills = skills;
    }

    public string Compose(string baseInstructions)
    {
        var builder = new StringBuilder(baseInstructions);
        builder.Append("\n\n## GPTino house rules\n").Append(HouseRules.Text);
        var index = _skills.IndexText();
        if (index.Length > 0)
        {
            builder.Append("\n\n## Built-in skills (fetch with skill_read; bodies are not in context)\n");
            builder.Append(index);
        }
        return _projectContext.Compose(builder.ToString());
    }
}

/// <summary>
/// Plugin-level modeling conventions. These ship compiled into the plugin so any Rhino
/// file, machine, or user gets identical behavior — the performance and quality
/// baseline of GPTino rather than per-project preference (those belong in rules.md).
/// </summary>
public static class HouseRules
{
    public const string Text = """
        Grasshopper authoring conventions (mandatory):
        - Parametric by default: expose every design-driving constant (spacing, counts, heights, section sizes) as a
          labeled Number Slider wired into your script inputs. Never hardcode a value the user may want to tune.
          Give each slider a meaningful nickname; place sliders left of the component they feed.
        - Label everything: give every script component and each of its outputs a meaningful nickname describing
          what flows through it. Unlabeled outputs are a defect.
        - Baking is standardized: never write ad-hoc bake code. Fetch the vetted bake_manager.py skill with
          skill_read, create it as a Python 3 component, and wire a Button component into its bake input.
          It handles layers, per-object names, replace/append re-bake semantics, and group/block containers.
          Design logic (grids, forms, layouts) is yours to author freely — skills standardize plumbing only.
        - The Rhino 8 Python 3 script component proxy GUID is 719467e6-7cf5-4848-99b0-c5dd57e5442c; use it directly
          with canvas.create instead of searching the catalog. Number Slider values are set with canvas.setNumberSlider.

        Speed discipline (mandatory):
        - Author a Python component's whole payload chain (create, setSource, optional setSchema/setTyping, execute)
          in one deliberation, then submit the ChangeSets back to back without re-reading state in between.
        - After a job commits, job_status returns committed { snapshotId, revision, resources[].fingerprint }.
          Base the next ChangeSet's expectedSnapshotId, baseSnapshotRevision, and write expectations on those values
          instead of calling snapshot_read again.
        - If a submit is rejected or blocked as stale, the error message carries the current fingerprint or current
          snapshotId. Correct only those values and resubmit immediately; do not restart discovery.
        """;
}
