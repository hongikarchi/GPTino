# Steel member axis extraction from a structural-company 3dm (unit-prototype blocks).
# Pattern: each section-mark layer holds one prototype solid at origin (1000mm tall)
# plus InstanceReferences whose Xform places/scales it -> the true member axis is the
# prototype axis (0,0,0)-(0,0,1000) pushed through the instance transform. Exact, no
# skeletonization. Loose Breps (braces etc.) are counted but skipped in v1.
import sys
import json
import collections
import rhino3dm

path = sys.argv[1]
out_path = sys.argv[2]

model = rhino3dm.File3dm.Read(path)
layers = {l.Index: l.FullPath for l in model.Layers}
idefs = {}
for i, idef in enumerate(model.InstanceDefinitions):
    idefs[i] = idef

members = []
skipped = collections.Counter()
proto_dims = {}

def xform_point(xf, p):
    x = xf.M00*p[0] + xf.M01*p[1] + xf.M02*p[2] + xf.M03
    y = xf.M10*p[0] + xf.M11*p[1] + xf.M12*p[2] + xf.M13
    z = xf.M20*p[0] + xf.M21*p[1] + xf.M22*p[2] + xf.M23
    w = xf.M30*p[0] + xf.M31*p[1] + xf.M32*p[2] + xf.M33
    if w not in (0.0, 1.0):
        x, y, z = x/w, y/w, z/w
    return (x, y, z)

# prototype outer dims per steel layer (from the origin Brep)
for obj in model.Objects:
    lp = layers.get(obj.Attributes.LayerIndex, "")
    if "철골" not in lp:
        continue
    g = obj.Geometry
    if type(g).__name__ in ("Brep", "Extrusion"):
        bb = g.GetBoundingBox()
        near_origin = abs(bb.Min.X) < 5000 and abs(bb.Min.Y) < 5000 and abs(bb.Min.Z) < 100
        if near_origin and 990.0 <= (bb.Max.Z - bb.Min.Z) <= 1010.0:
            faces = None
            try:
                faces = len(list(g.Faces))
            except Exception:
                pass
            proto_dims[lp] = {
                "bx": round(bb.Max.X - bb.Min.X, 1),
                "by": round(bb.Max.Y - bb.Min.Y, 1),
                "faces": faces,
            }

for obj in model.Objects:
    lp = layers.get(obj.Attributes.LayerIndex, "")
    if "철골" not in lp:
        continue
    g = obj.Geometry
    tname = type(g).__name__
    if tname == "InstanceReference":
        xf = g.Xform
        a = xform_point(xf, (0.0, 0.0, 0.0))
        b = xform_point(xf, (0.0, 0.0, 1000.0))
        L = ((a[0]-b[0])**2 + (a[1]-b[1])**2 + (a[2]-b[2])**2) ** 0.5
        mark = lp.split("::")[-1]
        members.append({
            "mark": mark,
            "layer": lp,
            "ax": [round(v, 1) for v in a],
            "bx": [round(v, 1) for v in b],
            "len_mm": round(L, 1),
        })
    else:
        bb = g.GetBoundingBox()
        near_origin = abs(bb.Min.X) < 5000 and abs(bb.Min.Y) < 5000
        if near_origin or tname not in ("Brep", "Extrusion"):
            skipped[("prototype" if near_origin else "loose") + ":" + tname] += 1
            continue
        # Loose solid (braces etc.): approximate the axis by PCA over the brep vertex
        # cloud - slender members have a dominant principal direction; endpoints are the
        # extreme projections of the vertices onto that axis.
        try:
            verts = [(v.Location.X, v.Location.Y, v.Location.Z) for v in g.Vertices]
        except Exception:
            verts = []
        if len(verts) < 4:
            skipped["loose:no-vertices"] += 1
            continue
        n = float(len(verts))
        cx = sum(v[0] for v in verts) / n
        cy = sum(v[1] for v in verts) / n
        cz = sum(v[2] for v in verts) / n
        sxx = syy = szz = sxy = sxz = syz = 0.0
        for vx, vy, vz in verts:
            dx, dy, dz = vx - cx, vy - cy, vz - cz
            sxx += dx*dx; syy += dy*dy; szz += dz*dz
            sxy += dx*dy; sxz += dx*dz; syz += dy*dz
        # power iteration for the dominant eigenvector of the covariance matrix
        ex, ey, ez = 1.0, 1.0, 1.0
        for _ in range(50):
            nx = sxx*ex + sxy*ey + sxz*ez
            ny = sxy*ex + syy*ey + syz*ez
            nz = sxz*ex + syz*ey + szz*ez
            mag = (nx*nx + ny*ny + nz*nz) ** 0.5
            if mag == 0:
                break
            ex, ey, ez = nx/mag, ny/mag, nz/mag
        ts = [ (vx-cx)*ex + (vy-cy)*ey + (vz-cz)*ez for vx, vy, vz in verts ]
        t0, t1 = min(ts), max(ts)
        if (t1 - t0) < 300.0:
            skipped["loose:too-short"] += 1
            continue
        a = (cx + t0*ex, cy + t0*ey, cz + t0*ez)
        b = (cx + t1*ex, cy + t1*ey, cz + t1*ez)
        mark = lp.split("::")[-1]
        members.append({
            "mark": mark,
            "layer": lp,
            "ax": [round(v, 1) for v in a],
            "bx": [round(v, 1) for v in b],
            "len_mm": round(t1 - t0, 1),
            "approx": True,
        })

# Dedupe pass: real assemblies are modeled as several solids (main member + cover
# plates / gussets) and some braces exist both as instances and loose solids — each
# yielding a near-identical axis. Merge axes that are near-parallel and near-coincident;
# prefer exact (instance-derived) axes over PCA, then longer over shorter.
def _u(m):
    a, b = m["ax"], m["bx"]
    L = max(m["len_mm"], 1e-6)
    return [(b[i] - a[i]) / L for i in range(3)]

def _mid(m):
    return [(m["ax"][i] + m["bx"][i]) / 2.0 for i in range(3)]

members.sort(key=lambda m: (bool(m.get("approx")), -m["len_mm"]))
kept = []
merged_away = 0
for m in members:
    um, mm_ = _u(m), _mid(m)
    dup = False
    for k in kept:
        if k["mark"].split(" ")[0] != m["mark"].split(" ")[0]:
            continue
        uk = _u(k)
        dot = abs(sum(um[i] * uk[i] for i in range(3)))
        if dot < 0.9986:  # > ~3 deg apart
            continue
        mk = _mid(k)
        if sum((mm_[i] - mk[i]) ** 2 for i in range(3)) ** 0.5 < 250.0:
            dup = True
            break
    if dup:
        merged_away += 1
    else:
        kept.append(m)
members = kept

by_mark = collections.Counter(m["mark"] for m in members)
summary = {
    "units": str(model.Settings.ModelUnitSystem),
    "member_count": len(members),
    "merged_duplicate_axes": merged_away,
    "by_mark": dict(by_mark),
    "skipped": dict(skipped),
    "prototype_outer_dims_mm": proto_dims,
}
with open(out_path, "w", encoding="utf-8") as h:
    json.dump({"summary": summary, "members": members}, h, ensure_ascii=False, indent=1)

print(json.dumps(summary, ensure_ascii=False, indent=1))
lens = sorted(m["len_mm"] for m in members)
if lens:
    print("axis lengths mm: min=%.0f med=%.0f max=%.0f" % (lens[0], lens[len(lens)//2], lens[-1]))
