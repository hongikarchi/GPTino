# Grasshopper authoring reference — idioms, GUIDs, and pitfalls (adapt freely)

Reference notes, not fixed code. Design logic stays yours; these save discovery round-trips.

## Language choice (policy)

- **C# is the default** for script components: compiled once, native RhinoCommon speed, no
  interpreter boot, no pip, and compile errors surface immediately in job diagnostics.
  Scaffold and idioms: skill `gh-csharp-cookbook.md`.
- **Python 3 (CPython)** only when the task genuinely needs numpy/scipy or another
  C-extension package. Expect ~10x slower RhinoCommon-dense code and a session-first-use
  interpreter boot. **Never put `# r:` package requirements in shipped scripts** — they
  trigger pip resolution that blocks file open; rely on pre-installed packages only.
- Prefer native components over any script for standard operations (transforms, lofts,
  booleans, tree ops) expressible in roughly a dozen components or fewer. Consolidate
  logic into ONE script per logical stage instead of many micro-scripts — every script
  component adds fixed load-time and solve-time overhead; keep scripts per definition
  in the single digits.

## Well-known component type GUIDs (use with canvas.create, skip component_catalog)

| Component | Type GUID |
|---|---|
| C# Script (Rhino 8, default) | `b6ba1144-02d6-4a2d-b53c-ec62e290eeb7` |
| Python 3 Script (Rhino 8 CPython) | `719467e6-7cf5-4848-99b0-c5dd57e5442c` |
| Number Slider | `57da07bd-ecab-415d-9d86-af36d7073abc` |
| Panel | `59e0b89a-e487-49f8-bab8-b5bab16be14c` |

For Button (bake trigger), Boolean Toggle, and anything else, resolve the GUID with one
component_catalog search. If a create is rejected for an unknown GUID, fall back to
component_catalog — installed sets vary.

## Input access decision rules (item/list/tree)

- One value per solve (a slider, a single point) → **item**.
- The script consumes a whole collection at once (all points of a grid, all curves to
  loft) → **list**; vectorize inside the script instead of letting the solver iterate
  the component per item.
- Branch structure matters (per-floor, per-row grouping) → **tree**; otherwise flatten
  upstream and use list. When unsure, inspect the upstream output (inspect_outputs or
  the job result's committed.outputs): one flat branch → list; multiple paths → tree.

## Python 3 script component idioms

- First line of source must be `#! python 3` (GPTino prepends it if missing).
- Inputs arrive as plain variables named after the input nicknames; outputs are assigned
  to variables named after output nicknames. Set input access (item/list/tree) via
  python.setTyping; list inputs arrive as Python lists.
- Tree access: prefer flattening upstream or `treehelpers`:
  `from ghpythonlib import treehelpers; nested = treehelpers.tree_to_list(x)`.
- `import Rhino.Geometry as rg` for geometry; construct with explicit floats
  (`rg.Point3d(float(x), 0.0, 0.0)`). Call RhinoCommon directly — avoid
  rhinoscriptsyntax and ghpythonlib.components in hot paths (GUID/proxy-document
  round-trips).
- Writing to the Rhino document from a script (baking) uses
  `doc = Rhino.RhinoDoc.ActiveDoc` — this is legitimate inside a user-run GH script,
  unlike GPTino bridge operations which must never touch the active document.

## Parametric layout conventions

- One labeled Number Slider per design constant, placed left of its consumer.
  Slider setup needs value, minimum, maximum, decimalPlaces (canvas.setNumberSlider).
- Wire sliders into script inputs rather than baking constants into source, so the
  user can tune without another agent turn.
- Nickname every script output; downstream users read the canvas by labels.

## Bake pipeline

Use the bake_manager.py skill (skill_read) as the project's single bake component:
geometry + layer + name_prefix + mode(replace|append) + container(none|group|block)
+ base_point + bake(Button). Re-running with mode=replace updates the previous bake
in place (GUID-preserving where possible) instead of duplicating objects.
