# Grasshopper authoring reference — idioms, GUIDs, and pitfalls (adapt freely)

Reference notes, not fixed code. Design logic stays yours; these save discovery round-trips.

## Well-known component type GUIDs (use with canvas.create, skip component_catalog)

| Component | Type GUID |
|---|---|
| Python 3 Script (Rhino 8 CPython) | `719467e6-7cf5-4848-99b0-c5dd57e5442c` |
| Number Slider | `57da07bd-ecab-415d-9d86-af36d7073abc` |
| Panel | `59e0b89a-e487-49f8-bab8-b5bab16be14c` |

For Button (bake trigger), Boolean Toggle, and anything else, resolve the GUID with one
component_catalog search. If a create is rejected for an unknown GUID, fall back to
component_catalog — installed sets vary.

## Python 3 script component idioms

- First line of source must be `#! python 3` (GPTino prepends it if missing).
- Inputs arrive as plain variables named after the input nicknames; outputs are assigned
  to variables named after output nicknames. Set input access (item/list/tree) via
  python.setTyping; list inputs arrive as Python lists.
- Tree access: prefer flattening upstream or `treehelpers`:
  `from ghpythonlib import treehelpers; nested = treehelpers.tree_to_list(x)`.
- `import Rhino.Geometry as rg` for geometry; construct with explicit floats
  (`rg.Point3d(float(x), 0.0, 0.0)`).
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
