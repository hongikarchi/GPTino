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
        - A Python component is authored as an ORDERED chain of ChangeSets. executePython is ALWAYS the last step
          and the input sliders MUST be wired before it. Plan the whole chain in one deliberation, then submit:
          1) createComponent for the script component AND every input Number Slider (one ChangeSet).
          2) updatePythonSource + setComponentIo in ONE ChangeSet — DO NOT execute here. The script references
             every input variable by name and coerces it DEFENSIVELY, because an input socket that is not yet wired
             evaluates to None: count = int(count) if count is not None else <default>;
             spacing = float(spacing) if spacing is not None else <default>. Assign outputs to variables named after
             the output sockets. setComponentIo appends sockets whose names exactly match the script's input/output
             variables; set access (item/list/tree) correctly. TYPE HINTS MATTER FOR GEOMETRY: a scalar from a
             slider stays generic (leave typeHint object/int/double and coerce in-script), but ANY socket that
             carries geometry — especially one wired to or from another component — MUST use the geometry type
             hint (point3d, vector3d, line, curve, circle, arc, plane, polyline, box, brep, mesh, surface,
             geometry) on BOTH the producing output and the consuming input, or the receiver gets an
             untyped/Guid value and pt.X fails. updatePythonSource only stages the source and never runs it, so
             referencing sockets that setComponentIo is about to create in the same ChangeSet is safe.
          3) snapshot_read the component (scope wireify:<component-guid>) to read the Grasshopper-assigned input
             socket UUIDs — they are NOT the placeholder ids you supplied, and you cannot wire without them. Never
             reconstruct or guess a socket UUID.
          4) createWire from each slider to its matching input socket (using the exact parameterId from step 3),
             in ONE ChangeSet (wire writes only — a wire cannot share a ChangeSet with a Python source/IO/value
             write). If a wire reports the parameter was not found, the error lists the available socket
             name=id pairs — wire to that exact id.
          5) executePython in its OWN final ChangeSet (a Python value write must be alone), AFTER the wires commit.
             Executing a component whose inputs are still unwired (None) is a defect — that is why wiring is step 4
             and execution is step 5.
        - Optimistic-concurrency bookkeeping is automatic — do NOT carry snapshotId/revision/fingerprints between
          ChangeSets. Set expectedSnapshotId to "gptino:auto", baseSnapshotRevision to -1, and every writeSet/readSet
          expectedFingerprint to "gptino:auto". The server fills the real values from your own session's last commit,
          so a Python source→schema→execute→wire chain submits back to back with no re-reads. Two exceptions still
          need the concrete fingerprint from the previous commit (in both payload and writeSet): value/geometry writes
          (setNumberSlider, moveComponent, delete, Rhino transform/upsert) and create targets (which use "gptino:absent").
        - "gptino:auto" fills a value only when THIS session already committed the resource and it is unchanged. If a
          genuine foreign change (another session or a manual Grasshopper edit) touched it, the job is Blocked with the
          current fingerprint — re-read that one resource and resubmit it with the concrete value; do not restart discovery.
        - Acceptance predicates are OPTIONAL: submit "acceptancePredicates":[] and the server attaches the standard
          set automatically (creates/bakes → objectExists; deletes → objectAbsent; wires → wireExists/wireAbsent;
          everything else → runtimeErrorAbsent). If you declare your own, the kinds are exactly:
          fingerprintEquals | runtimeErrorAbsent | wireExists | wireAbsent | objectExists | objectAbsent — never
          predict a future fingerprint with fingerprintEquals and never invent per-operation "value updated"
          predicates.
        - A job that ends state=failed WITH an "applied" block means the writes physically landed but were not
          committed — script compile/runtime errors report this way. This is the normal iterate loop, not a dead
          end: read diagnostics[] (every error names its operationId), fix the source, and resubmit with
          gptino:auto — the server ledger already tracks the applied state, so the retry is not stale-blocked.
          A red component never commits; only job_status=committed means the change is verified and in history.
        - Two consecutive Failed/Blocked jobs for the same intent → STOP, show the exact job message to the user,
          and ask how to proceed. Do not re-draft artifacts against a Blocked job.
        - Use this exact ChangeSet shape on the first submit (property names are exact; no other spellings exist):
          {"changeSetId":"<uuid>","projectId":"<from snapshot_read>","sessionId":"<from snapshot_read>",
           "baseSnapshotRevision":-1,"baseGitCommit":null,"dependencies":[],
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
