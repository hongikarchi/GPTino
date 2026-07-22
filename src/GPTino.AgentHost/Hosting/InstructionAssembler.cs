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
        - Python component IO, in this order within one contiguous ChangeSet:
          1) updatePythonSource first — the script must reference every input variable by name and coerce it
             (count = int(count); spacing = float(spacing)), because sockets are generic (name-bound, not
             strictly typed). Assign outputs to variables named after the output sockets.
          2) setComponentIo second — append sockets whose names exactly match the script's input/output variables.
             Type hints are advisory (sockets are generic); set access (item/list/tree) correctly for list inputs.
          3) executePython last.
          Read current sockets first with one scoped snapshot_read (scope wireify:<component-guid>); preserve every
          existing socket UUID and order; only appended sockets get new UUIDs.
        - Acceptance predicate kinds are exactly: fingerprintEquals | runtimeErrorAbsent | wireExists | wireAbsent |
          objectExists | objectAbsent. No other spelling parses. For updatePythonSource/executePython use
          {"name":"no runtime errors","kind":"runtimeErrorAbsent","resource":null,"expectedValue":null}.
          objectExists/objectAbsent take an object resource and expectedValue null; only fingerprintEquals uses
          expectedValue. Never predict a future fingerprint with fingerprintEquals. The standard predicate set is:
          creates → one objectExists per created component; wires → wireExists/wireAbsent; everything else
          (setValue, moveComponent, setGroup, python source/schema/execute) → the single runtimeErrorAbsent example.
          Do not invent per-operation "value updated" predicates.
        - Use this exact ChangeSet shape on the first submit (property names are exact; no other spellings exist):
          {"changeSetId":"<uuid>","projectId":"<from snapshot_read>","sessionId":"<from snapshot_read>",
           "baseSnapshotRevision":7,"baseGitCommit":null,"dependencies":[],
           "readSet":[],"writeSet":[{"resource":{"kind":"grasshopperComponent","id":"<uuid>","field":"*"},
           "expectedFingerprint":"gptino:absent"}],
           "operations":[{"operationId":"create-x","kind":"createComponent","owner":"cordyceps",
           "reads":[],"writes":[{"kind":"grasshopperComponent","id":"<uuid>","field":"*"}],
           "reversible":false,"payloadArtifact":"operations/create-x.json"}],
           "acceptancePredicates":[{"name":"create-x exists","kind":"objectExists",
           "resource":{"kind":"grasshopperComponent","id":"<uuid>","field":"*"},"expectedValue":null}],
           "rollbackBeforeImages":[],"createdAt":"<iso8601>"}
        """;
}
