#! python 3
# GH->Rhino bake manager: layers, per-object names, replace/append re-bake, group/block containers.
#
# GPTino built-in skill. Create this as a Rhino 8 Python 3 script component and wire:
#   inputs  (set access as noted, type hints ghdoc/objects where available):
#     geometry    (list)  Geometry to bake (any GeometryBase; nulls skipped)
#     layer       (item)  Target layer full path, e.g. "GPTino::Panels" ("" = current layer)
#     name_prefix (item)  Family key + name prefix; objects are named {name_prefix}-{i:03d}
#     mode        (item)  "replace" (default) = re-bake updates this family idempotently,
#                         "append" = always add new objects (design-option stacking)
#     container   (item)  "none" (default) | "group" | "block"
#     base_point  (item)  Block base point (Point3d); required for container="block"
#     bake        (item)  Boolean; wire a Button component here. False = dry-run report.
#   outputs:
#     report      (item)  What happened / what would happen
#     baked_ids   (list)  Guids of live objects belonging to this family after the bake
#
# Family identity: every baked object carries user text "gptino_bake_family" = name_prefix,
# so replace mode can find and update its own previous output without touching anything else.

import Rhino
import Rhino.Geometry as rg
import System

doc = Rhino.RhinoDoc.ActiveDoc
FAMILY_KEY = "gptino_bake_family"


def ensure_layer(full_path):
    if not full_path:
        return doc.Layers.CurrentLayerIndex
    parent = System.Guid.Empty
    index = -1
    accumulated = []
    for token in [part.strip() for part in full_path.split("::") if part.strip()]:
        accumulated.append(token)
        joined = "::".join(accumulated)
        existing = doc.Layers.FindByFullPath(joined, -1)
        if existing >= 0:
            index = existing
            parent = doc.Layers[existing].Id
            continue
        layer = Rhino.DocObjects.Layer()
        layer.Name = token
        if parent != System.Guid.Empty:
            layer.ParentLayerId = parent
        index = doc.Layers.Add(layer)
        parent = doc.Layers[index].Id
    return index


def family_objects(family):
    found = []
    for obj in doc.Objects:
        if obj.Attributes.GetUserString(FAMILY_KEY) == family:
            found.append(obj)
    return found


def make_attributes(family, ordinal, layer_index):
    attributes = doc.CreateDefaultAttributes()
    attributes.Name = "{}-{:03d}".format(family, ordinal)
    attributes.LayerIndex = layer_index
    attributes.SetUserString(FAMILY_KEY, family)
    return attributes


def add_geometry(geo, attributes):
    identifier = doc.Objects.Add(geo, attributes)
    return identifier if identifier != System.Guid.Empty else None


def replace_geometry(existing_id, geo):
    """Typed Replace preserves the object GUID (annotations and references survive)."""
    obj_ref = Rhino.DocObjects.ObjRef(doc, existing_id)
    replacers = {
        rg.Brep: doc.Objects.Replace,
        rg.Curve: doc.Objects.Replace,
        rg.Mesh: doc.Objects.Replace,
        rg.Surface: doc.Objects.Replace,
        rg.Point: doc.Objects.Replace,
        rg.Extrusion: doc.Objects.Replace,
        rg.SubD: doc.Objects.Replace,
    }
    for geometry_type in replacers:
        if isinstance(geo, geometry_type):
            try:
                return bool(doc.Objects.Replace(obj_ref, geo))
            except Exception:
                return False
    return False


geometry = [g for g in (geometry or []) if g is not None]
mode = (mode or "replace").strip().lower()
container = (container or "none").strip().lower()
family = (name_prefix or "gptino").strip()
if mode not in ("replace", "append"):
    mode = "replace"
if container not in ("none", "group", "block"):
    container = "none"

if not bake:
    existing_count = len(family_objects(family))
    report = "Dry run: would bake {} object(s) as family '{}' ({} existing), mode={}, container={}. Set bake=True.".format(
        len(geometry), family, existing_count, mode, container)
    baked_ids = [obj.Id for obj in family_objects(family)]
else:
    undo = doc.BeginUndoRecord("GPTino bake: " + family)
    try:
        layer_index = ensure_layer(layer)
        replaced = 0
        added = 0
        removed = 0

        if container == "block":
            if base_point is None:
                raise Exception("container='block' requires base_point")
            existing_index = doc.InstanceDefinitions.Find(family, True)
            duplicates = [g.Duplicate() for g in geometry]
            attr_list = [make_attributes(family, i, layer_index) for i in range(len(duplicates))]
            if existing_index is not None and mode == "replace":
                doc.InstanceDefinitions.ModifyGeometry(existing_index.Index, duplicates, attr_list)
                definition_index = existing_index.Index
                replaced = len(duplicates)
            else:
                definition_index = doc.InstanceDefinitions.Add(
                    family if existing_index is None else "{}-{}".format(family, System.DateTime.Now.Ticks),
                    "GPTino baked block", base_point, duplicates, attr_list)
                added = len(duplicates)
            instances = [obj for obj in family_objects(family)
                         if isinstance(obj, Rhino.DocObjects.InstanceObject)]
            if not instances or mode == "append":
                transform = rg.Transform.Translation(base_point - rg.Point3d.Origin)
                doc.Objects.AddInstanceObject(
                    definition_index, transform, make_attributes(family, len(instances), layer_index))
        else:
            existing = family_objects(family) if mode == "replace" else []
            for i, geo in enumerate(geometry):
                attributes = make_attributes(family, i, layer_index)
                if i < len(existing):
                    obj = existing[i]
                    if replace_geometry(obj.Id, geo):
                        obj.Attributes.Name = attributes.Name
                        obj.Attributes.LayerIndex = layer_index
                        obj.CommitChanges()
                        replaced += 1
                        continue
                    doc.Objects.Delete(obj, True)
                    removed += 1
                if add_geometry(geo, attributes) is not None:
                    added += 1
            for obj in existing[len(geometry):]:
                doc.Objects.Delete(obj, True)
                removed += 1

            if container == "group":
                group_index = doc.Groups.Find(family)
                if group_index < 0:
                    group_index = doc.Groups.Add(family)
                for obj in family_objects(family):
                    if not isinstance(obj, Rhino.DocObjects.InstanceObject):
                        doc.Groups.AddToGroup(group_index, obj.Id)

        doc.Views.Redraw()
        baked_ids = [obj.Id for obj in family_objects(family)]
        report = "Baked family '{}': {} replaced, {} added, {} removed; {} live object(s); mode={}, container={}.".format(
            family, replaced, added, removed, len(baked_ids), mode, container)
    finally:
        doc.EndUndoRecord(undo)
