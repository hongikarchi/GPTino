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

    // internal (not private) so a parity test can assert this compiled fallback stays byte-identical
    // to assets/instructions/house-rules.md — the two are edited together and must never drift.
    internal const string DefaultText = """
        Grasshopper authoring conventions (mandatory):
        - Parametric by default: expose every design-driving constant (spacing, counts, heights, section sizes) as a
          labeled Number Slider wired into your script inputs. Never hardcode a value the user may want to tune.
          Give each slider a meaningful nickname; declare each slider's objectId in the consuming component's
          canvas.create autoUpstream so server-side auto-placement puts sliders left of the component they feed.
        - Label everything: give every script component and each of its outputs a meaningful nickname describing
          what flows through it. Unlabeled outputs are a defect.
        - Baking is standardized: never write ad-hoc bake code. Fetch the vetted bake_manager.py skill with
          skill_read, create it as a Python 3 component, and wire a Button component into its bake input.
          It handles layers, per-object names, replace/append re-bake semantics, and group/block containers.
          Design logic (grids, forms, layouts) is yours to author freely — skills standardize plumbing only.
        - Paneling/facade tasks: fetch gh-paneling-cookbook.md with skill_read before authoring — it has vetted
          isotrim UV-grid, attractor-opening, and thickness-solid (CreateOffsetBrep) RhinoCommon idioms. Adapt
          them rather than deriving each geometry algorithm from scratch; the design intent stays yours.
        - Structural analysis tasks: fetch gh-karamba-cookbook.md AND structural-analysis.md with skill_read
          before authoring — the cookbook has the vetted Karamba3D Toolkit workflow, drift-safe API idioms, and
          solver-script rules (unwired-input guard; assign the solved output only on a successful solve); the
          domain guide has model-input rules, ULS/SLS load-combination discipline, and deflection-limit
          conditions. Verdict math (utilization / deflection pass-fail) is never improvised: use Karamba's own
          Utilization/OptiCroSec or wire the vetted structural_check.py payload verbatim, like bake_manager.py.
          Follow their rules exactly; the design intent stays yours.
        - LANGUAGE POLICY: author script components in C# BY DEFAULT (proxy GUID
          b6ba1144-02d6-4a2d-b53c-ec62e290eeb7 with canvas.create; runtime "csharp"; skill
          gh-csharp-cookbook.md has the scaffold and idioms). C# compiles once and runs at native speed with no
          interpreter boot, no pythonnet overhead, and no pip stalls — and compile errors come back immediately
          in diagnostics[] for you to fix. Use Python 3 (GUID 719467e6-7cf5-4848-99b0-c5dd57e5442c; runtime
          "cpython3") ONLY when the task genuinely needs numpy/scipy or another C-extension package. NEVER put
          '# r:' package requirements in shipped scripts — they block file open on pip resolution; use
          pre-installed packages only. Number Slider values are set with canvas.setNumberSlider.

        Focus references (chat): when your chat text points at specific Rhino objects (an ask-back about
        ambiguous geometry, a problem area, a proposed alternative), wrap the reference as
        [[focus:<objectId>[,<objectId>...]|<short label>]] using ONLY objectIds a tool actually returned
        (rhino_list, referenced selections, job results). The panel renders it as a chip the user clicks
        to select+zoom (or isolate) those objects in the viewport — point at geometry, don't describe
        locations in words. Never invent ids; a few markers per message at most.
        When you propose ALTERNATIVES (design options, fixes to choose between), mark each one
        [[alt:<id>|<short label>]] with a stable id ([A-Za-z0-9._-], e.g. alt-upsize) that names a
        variant you actually produced; clicking it shows that variant. Explain each alt in prose,
        then let the chips carry the choice.

        Design intent (mandatory):
        - Selected geometry is INPUT, not a parameter to reinvent. When the user says "use the objects I
          selected in Rhino/Grasshopper as input," create a referenceRhinoObjects parameter for those exact
          Rhino object ids (paramType matching the selection) and wire it downstream as the geometry input.
          Do NOT replace the user's selection with sliders/parameters that regenerate similar geometry from
          scratch, and do NOT re-author it in a script — the selection is deliberate and carries information
          (exact curves, positions) you cannot reconstruct, and a live reference keeps updating if the user
          edits the Rhino object.
        - Respect openings and cutouts. When the design has an opening (oculus, window, entrance, void),
          there must be NO panel/surface covering it, and panels must follow the opening's real boundary
          curve — if the opening is an ellipse or free curve, do not approximate it with rectangles or leave
          panels overlapping it. Trim/cull panels against the opening curve.
        - Preserve data-tree structure when porting Python->C# (or any re-authoring). Match the original
          component's socket access (item/list/tree) and output data-tree paths exactly — a port that
          flattens a tree the original kept is a defect, even if the geometry looks similar.

        Heavy solve discipline (mandatory):
        - Every bridge operation has a hard 45-second budget. A Grasshopper solve that exceeds it freezes
          Rhino, dead-ends the job as recoveryRequired, and may leave the document half-applied — treat
          solve time as a scarce resource exactly like tokens.
        - Author solver-heavy surface work (NetworkSrf/Patch/Coons, full-surface splitting, multi-alternative
          panelization) INCREMENTALLY: first execute with deliberately small sampling/segment/count values
          exposed as sliders, verify committed.outputs, and only then raise the values. Never make the first
          execution the full-resolution one. The server ENFORCES this: a component that has never produced a
          committed solve whose count-like sliders multiply past ~10,000 elements is rejected before the write —
          run a low-resolution pass, let it commit, THEN raise the counts (an established component's ceiling is
          far higher). If you hit that rejection, lower the counts; do not resubmit the same values.
        - Solver domains stay native: environmental/physics solves and expensive surface fitting belong to
          native Grasshopper components wired into the definition, not to re-implementations inside one
          script. Script components are for geometry utilities that finish in seconds. ONE exception:
          structural analysis runs as a C# script calling the installed Karamba3D Toolkit API per
          gh-karamba-cookbook.md — a vetted library call, not a re-implementation; hand-rolling FE/solver
          math in a script stays forbidden.
        - Decompose non-trivial C# into a CHAIN OF STAGED COMPONENTS, not one monolith. Split the logic by
          stage (e.g. base geometry -> subdivide/panelize -> trim/detail); author each stage as its OWN C#
          component whose outputs feed the next stage's inputs, and build them one at a time: execute a
          stage, verify its committed.outputs and op_duration, THEN author the next stage that consumes it.
          A staged chain gives each stage a fresh 45s budget, a history checkpoint, and per-component
          Grasshopper caching (a downstream slider tweak re-solves only its stage). Use it for anything
          beyond a few seconds of compute; keep one monolithic component only for trivial utilities. Staging
          does NOT shrink a cold full-solve's total time (one UI thread) — it bounds and checkpoints it.
        - Target ~1 second per component. After executing a component, read its op_duration diagnostic; if it
          exceeds ~1s, split that component into smaller LOGICAL stages (by meaning — e.g. build vs subdivide
          vs trim/detail), re-execute, and re-check. Split by logic, NEVER arbitrarily: if one coherent
          logical unit still exceeds ~1s after a sensible split, accept it — do not force absurd micro-splits.
          The aim is to stop any single component becoming a long solve, not to shred the logic.
        - Wire in a LINEAR logical flow and group by stage. Each stage's output feeds the NEXT stage's input
          (stage1 -> stage2 -> stage3); do not re-plug the same upstream source into several stages when a
          linear pass can carry it through (e.g. if stage1 already consumed course_pitch, pass what stage2
          needs out of stage1 rather than re-wiring course_pitch into stage2). Rely on gptino:auto placement
          (list feeders in autoUpstream) for a clean left-to-right layout, and put each logical stage's
          components in their own named setGroup ("Base Surface", "Paneling", "Openings", "Bake", ...) so the
          canvas reads as the logic flow.
        - One heavy execute per ChangeSet, nothing else in it — isolate the expensive computation in its
          OWN component, executed and verified (committed.outputs) BEFORE you wire anything downstream. On a
          timeout, never resubmit as-is: reduce the workload (sampling, counts, extent) or split the
          expensive step into its own staged component, then raise resolution after a committed low-res pass.
        - Iterate a heavy downstream stage on the default recomputeDocument=false so only expired objects
          re-solve; reserve recomputeDocument=true (expire-all) for a genuine full rebuild — it re-solves
          the whole document inside one 45s-bounded block.
        - Bound your loops: estimate element counts before writing (a 100x100 grid is 10,000 iterations of
          whatever body you write). Quadratic pairwise passes over thousands of elements belong in RTree
          queries, not nested loops. For thousands of INDEPENDENT per-item geometry computations that would
          otherwise approach the budget, author the C# component with Parallel.For — see the multithreading
          section of gh-csharp-cookbook.md, and follow its crash-safety rules exactly (only Rhino.Geometry
          on worker threads; never touch RhinoDoc/ActiveDoc off the main thread).
        - Self-limiting budget guard: give every unbounded or large loop a budget so a runaway loop ABORTS
          ITSELF — nothing outside the script can stop a running solve (it holds Rhino's single UI thread), so
          the only escape is the script throwing from inside the loop (a thrown exception unwinds cleanly and
          Grasshopper reports it as a runtime error). Start a stopwatch and an iteration counter and check both
          at the top of each loop; throw when either is exceeded. C#: var __sw =
          System.Diagnostics.Stopwatch.StartNew(); long __i = 0; then per loop head if (__sw.ElapsedMilliseconds
          > 8000 || ++__i > 20000000) throw new System.TimeoutException("solve budget"). Python: import time;
          __t0 = time.time(); __i = 0; then per loop head if time.time() - __t0 > 8 or (__i := __i + 1) >
          20000000: raise TimeoutError("solve budget"). A truly unbounded loop (while(true) / for(;;) / while
          True) with no such guard and no break is rejected before the write.

        Speed discipline (mandatory):
        - A script component (C# by default; Python 3 only when the task needs it) is authored as an ORDERED chain of ChangeSets. Plan the whole chain in one
          deliberation, submit each ChangeSet with wait=true, and chain from each job result's committed block —
          never re-read the canvas between steps:
          1) createComponent for the script component AND every input Number Slider (one ChangeSet). Every
             create uses pivot:"gptino:auto" — never hand-pick coordinates unless the user asked for a specific
             location. On the script component's create, set autoUpstream to the objectIds of the sliders (and
             any upstream components) that feed it, so the server lays sliders left and the script to their right.
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
          3) connectWire from each slider to its matching input socket, in ONE ChangeSet (wire writes only — a
             wire cannot share a ChangeSet with a Python source/IO/value write). The Grasshopper-assigned socket
             UUIDs are ALREADY in step 2's job result under committed.sockets (inputs[].id / outputs[].id) —
             wire to those exact ids; never snapshot_read for them and never reconstruct or guess one. If a wire
             reports the parameter was not found, the error lists the available socket name=id pairs — wire to
             that exact id.
          4) executePython in its own final ChangeSet AFTER the wires commit — or skip it when step 2 already
             executed and step 3's job result shows healthy committed.outputs (the wire write solves inline).
             Executing a component whose inputs are still unwired (None) without defensive defaults is a defect.
        - Tidy the canvas as the LAST step. Once your authoring chain has committed, call arrange_layout ONCE with
          seedComponentIds set to the objectIds you created this turn. The server lays the whole connected dataflow
          cluster out left-to-right (inputs -> script stages -> outputs, stacked and grouped) from the wires and real
          sizes, so you NEVER hand-pick move coordinates or issue a manual canvas.move just to tidy up. It is a no-op
          when the cluster is already clean.
        - Orientation costs at most ONE snapshot_read per user request. Between chained submits, read fingerprints,
          socket ids, output data, and diagnostics from each job result's committed/applied block instead.
        - Optimistic-concurrency bookkeeping is automatic — do NOT carry snapshotId/revision/fingerprints between
          ChangeSets. Set expectedSnapshotId to "gptino:auto", baseSnapshotRevision to -1, and every writeSet/readSet
          expectedFingerprint to "gptino:auto". The server fills the real values from your own session's last write
          (committed or applied), so the whole script chain submits back to back with no re-reads. Two exceptions
          still need the concrete fingerprint from the previous result (in both payload and writeSet): value/geometry
          writes (setNumberSlider, moveComponent, delete, Rhino transform/upsert) and create targets ("gptino:absent").
        - "gptino:auto" fills a value only when THIS session already wrote the resource in THIS runtime and it is
          unchanged — the ledger does not survive restarts and never transfers between sessions. A component you
          authored yesterday, in another session, or in a recovered document is PRE-EXISTING now: its first touch
          must carry a concrete fingerprint. After ANY auto-decline or stale block, the very next submission MUST
          use the exact "Current fingerprint" value quoted in the decline message — never resubmit gptino:auto for
          that resource, never resubmit the old expected value, and never re-read the whole snapshot for it. Two
          identical declines in a row mean you ignored the quoted value: stop and re-read the message.
        - Acceptance predicates are OPTIONAL: submit "acceptancePredicates":[] and the server attaches the standard
          set automatically (creates/bakes → objectExists; deletes → objectAbsent; wires → wireExists/wireAbsent;
          everything else → runtimeErrorAbsent). If you declare your own, the kinds are exactly:
          fingerprintEquals | runtimeErrorAbsent | wireExists | wireAbsent | objectExists | objectAbsent |
          outputCountInRange | areaInRange | volumeInRange | dataTreeBranchCountInRange | geometryClosed |
          boundingBoxInRange — never predict a future fingerprint with fingerprintEquals and never invent
          per-operation "value updated" predicates.
          The semantic ones verify against the REAL post-solve output inspection (resource = the component):
          outputCountInRange/areaInRange/volumeInRange/dataTreeBranchCountInRange use expectedValue
          "outputName:min:max" (max may be "*"); boundingBoxInRange uses "outputName:axis:min:max"
          (axis = x|y|z|diagonal); geometryClosed uses expectedValue = the output name. Declare a semantic predicate
          SPARINGLY — only for an OBVIOUS, OBJECTIVE failure tied to the user's ask (e.g. the opening must be a
          real void → assert the panel count dropped), with GENEROUS bounds (">= 1", not "exactly 47"). It is a
          safety net, never a gate on normal work; a tight/wrong one just makes you loop. Subjective design
          quality is the human's call, never a predicate.
        - Goal-directed iteration (bounded): when a request has a clear objective, let the acceptance
          predicate(s) you declared define "done" — submit, read the result, and if a declared predicate
          fails, use its diagnostics to fix and resubmit, iterating until they pass. This loop is BOUNDED by
          the two-consecutive-no-progress rule below: after two attempts with no progress toward passing,
          STOP and report to the user with the failing predicate and job message. Never loop blindly, and
          never keep tightening a self-imposed predicate — a repeatedly-failing objective check is a signal
          to ask the user, not to grind.
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
