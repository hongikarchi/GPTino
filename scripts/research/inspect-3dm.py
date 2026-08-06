# Headless 3dm census for structural feasibility: layers, object types per layer,
# and steel-member geometry details (curve lengths / brep bboxes) for a target layer.
import sys
import collections
import rhino3dm

path = sys.argv[1]
target_hint = sys.argv[2].lower() if len(sys.argv) > 2 else None

model = rhino3dm.File3dm.Read(path)
print("units:", model.Settings.ModelUnitSystem)
layers = list(model.Layers)
print("layers:", len(layers))

by_layer = collections.defaultdict(collections.Counter)
objs_by_layer = collections.defaultdict(list)
for obj in model.Objects:
    li = obj.Attributes.LayerIndex
    g = obj.Geometry
    by_layer[li][type(g).__name__] += 1
    objs_by_layer[li].append(obj)

for layer in layers:
    counts = by_layer.get(layer.Index)
    if not counts:
        continue
    total = sum(counts.values())
    print("L%-3d %-45s n=%-5d %s" % (layer.Index, layer.FullPath, total, dict(counts)))

if target_hint:
    print("\n--- target layer detail (hint: %s) ---" % target_hint)
    for layer in layers:
        if target_hint not in layer.FullPath.lower():
            continue
        objs = objs_by_layer.get(layer.Index, [])
        if not objs:
            continue
        print("\n[%s] n=%d" % (layer.FullPath, len(objs)))
        lengths = []
        names = collections.Counter()
        for obj in objs[:2000]:
            g = obj.Geometry
            n = obj.Attributes.Name or ""
            if n:
                names[n] += 1
            if isinstance(g, rhino3dm.Curve):
                try:
                    pl = g.TryGetPolyline() if hasattr(g, "TryGetPolyline") else None
                except Exception:
                    pl = None
                try:
                    a = g.PointAtStart; b = g.PointAtEnd
                    L = ((a.X-b.X)**2 + (a.Y-b.Y)**2 + (a.Z-b.Z)**2) ** 0.5
                    lengths.append(L)
                except Exception:
                    pass
        if lengths:
            lengths.sort()
            print("  curve chord lengths: n=%d min=%.0f med=%.0f max=%.0f" % (
                len(lengths), lengths[0], lengths[len(lengths)//2], lengths[-1]))
        if names:
            print("  object names (top 15):")
            for k, v in names.most_common(15):
                print("    %4d x %s" % (v, k))
        # sample first few objects verbosely
        for obj in objs[:5]:
            g = obj.Geometry
            bb = g.GetBoundingBox()
            print("  sample: %-18s name='%s' bbox=(%.0f,%.0f,%.0f)-(%.0f,%.0f,%.0f)" % (
                type(g).__name__, obj.Attributes.Name or "",
                bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z))
