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
/// Plugin-level modeling conventions — the performance and quality baseline of GPTino rather
/// than per-project preference (those belong in rules.md). The authoritative text ships as
/// assets/instructions/house-rules.md so prompt tuning is a file edit, not a rebuild; the
/// compiled default below is the fallback and must be kept in sync when the asset changes.
/// </summary>
public static class HouseRules
{
    public static string Text { get; } = InstructionAssets.LoadOrFallback("house-rules.md", DefaultText);

    private const string DefaultText = """
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
        - LANGUAGE POLICY: author script components in C# BY DEFAULT (proxy GUID
          b6ba1144-02d6-4a2d-b53c-ec62e290eeb7 with canvas.create; runtime "csharp"; skill
          gh-csharp-cookbook.md has the scaffold and idioms). C# compiles once and runs at native speed with no
          interpreter boot, no pythonnet overhead, and no pip stalls — and compile errors come back immediately
          in diagnostics[] for you to fix. Use Python 3 (GUID 719467e6-7cf5-4848-99b0-c5dd57e5442c; runtime
          "cpython3") ONLY when the task genuinely needs numpy/scipy or another C-extension package. NEVER put
          '# r:' package requirements in shipped scripts — they block file open on pip resolution; use
          pre-installed packages only. Number Slider values are set with canvas.setNumberSlider.

        Speed discipline (mandatory):
        - A Python component is authored as an ORDERED chain of ChangeSets. Plan the whole chain in one
          deliberation, submit each ChangeSet with wait=true, and chain from each job result's committed block —
          never re-read the canvas between steps:
          1) createComponent for the script component AND every input Number Slider (one ChangeSet).
          2) updatePythonSource + setComponentIo in ONE ChangeSet. The script references every input variable by
             name and guards it DEFENSIVELY, because an input socket that is not yet wired arrives empty —
             Python: count = int(count) if count is not None else <default>; C#: use nullable inputs and
             coalesce, e.g. var n = (int)(count ?? <default>). Assign outputs to variables named after the
             output sockets. setComponentIo appends sockets whose names exactly match the script's input/output
             variables; set access (item/list/tree) correctly. TYPE HINTS MATTER FOR GEOMETRY: a scalar from a
             slider stays generic (leave typeHint object/int/double and coerce in-script), but ANY socket that
             carries geometry — especially one wired to or from another component — MUST use the geometry type
             hint (point3d, vector3d, line, curve, circle, arc, plane, polyline, box, brep, mesh, surface,
             geometry) on BOTH the producing output and the consuming input, or the receiver gets an
             untyped/Guid value and pt.X fails. updatePythonSource only stages the source and never runs it, so
             referencing sockets that setComponentIo is about to create in the same ChangeSet is safe. You MAY
             append executePython at the end of this same ChangeSet when the defensive defaults make an unwired
             run meaningful — its diagnostics and outputs return in the same job result.
          3) createWire from each slider to its matching input socket, in ONE ChangeSet (wire writes only — a
             wire cannot share a ChangeSet with a Python source/IO/value write). The Grasshopper-assigned socket
             UUIDs are ALREADY in step 2's job result under committed.sockets (inputs[].id / outputs[].id) —
             wire to those exact ids; never snapshot_read for them and never reconstruct or guess one. If a wire
             reports the parameter was not found, the error lists the available socket name=id pairs — wire to
             that exact id.
          4) executePython in its own final ChangeSet AFTER the wires commit — or skip it when step 2 already
             executed and step 3's job result shows healthy committed.outputs (the wire write solves inline).
             Executing a component whose inputs are still unwired (None) without defensive defaults is a defect.
        - Orientation costs at most ONE snapshot_read per user request. Between chained submits, read fingerprints,
          socket ids, output data, and diagnostics from each job result's committed/applied block instead.
        - Optimistic-concurrency bookkeeping is automatic — do NOT carry snapshotId/revision/fingerprints between
          ChangeSets. Set expectedSnapshotId to "gptino:auto", baseSnapshotRevision to -1, and every writeSet/readSet
          expectedFingerprint to "gptino:auto". The server fills the real values from your own session's last write
          (committed or applied), so the whole Python chain submits back to back with no re-reads. Two exceptions
          still need the concrete fingerprint from the previous result (in both payload and writeSet): value/geometry
          writes (setNumberSlider, moveComponent, delete, Rhino transform/upsert) and create targets ("gptino:absent").
        - "gptino:auto" fills a value only when THIS session already wrote the resource and it is unchanged. If a
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
        - Use this exact ChangeSet shape on the first submit (property names are exact; no other spellings exist;
          acceptancePredicates stays [] — the server attaches the standard set):
          {"changeSetId":"<uuid>","projectId":"<from snapshot_read>","sessionId":"<from snapshot_read>",
           "baseSnapshotRevision":-1,"baseGitCommit":null,"dependencies":[],
           "readSet":[],"writeSet":[{"resource":{"kind":"grasshopperComponent","id":"<uuid>","field":"*"},
           "expectedFingerprint":"gptino:absent"}],
           "operations":[{"operationId":"create-x","kind":"createComponent","owner":"cordyceps",
           "reads":[],"writes":[{"kind":"grasshopperComponent","id":"<uuid>","field":"*"}],
           "reversible":false,"payloadArtifact":"operations/create-x.json"}],
           "acceptancePredicates":[],
           "rollbackBeforeImages":[],"createdAt":"<iso8601>"}
        """;
}
