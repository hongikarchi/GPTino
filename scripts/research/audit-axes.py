# Axis-extraction audit: does every extracted axis actually lie inside its source
# instance's bounding box, and is the unit-prototype assumption true per block definition?
import json
import math
import rhino3dm

PATH = r"C:\Users\user\Desktop\GPTino\260803 main ms.3dm"
model = rhino3dm.File3dm.Read(PATH)
layers = {l.Index: l.FullPath for l in model.Layers}

def xform_point(xf, p):
    x = xf.M00*p[0] + xf.M01*p[1] + xf.M02*p[2] + xf.M03
    y = xf.M10*p[0] + xf.M11*p[1] + xf.M12*p[2] + xf.M13
    z = xf.M20*p[0] + xf.M21*p[1] + xf.M22*p[2] + xf.M23
    return (x, y, z)

# 1) verify the unit-prototype assumption per definition actually used by steel layers
print("--- instance definition audit ---")
defs_seen = {}
for obj in model.Objects:
    lp = layers.get(obj.Attributes.LayerIndex, "")
    if "철골" not in lp or type(obj.Geometry).__name__ != "InstanceReference":
        continue
    di = obj.Geometry.ParentIdefId
    defs_seen.setdefault(str(di), lp)
print("distinct definitions used by steel instances:", len(defs_seen))

idefs_by_id = {}
for idef in model.InstanceDefinitions:
    idefs_by_id[str(idef.Id)] = idef

checked = 0
for did, lp in list(defs_seen.items())[:8]:
    idef = idefs_by_id.get(did)
    if idef is None:
        print("  def %s (%s): NOT FOUND in table" % (did[:8], lp.split('::')[-1]))
        continue
    ids = idef.GetObjectIds()
    bbs = []
    for oid in ids:
        o = model.Objects.FindId(str(oid))
        if o is not None:
            bb = o.Geometry.GetBoundingBox()
            bbs.append(bb)
    if not bbs:
        print("  def %s (%s): %d objs, geometry not resolvable" % (did[:8], lp.split('::')[-1], len(ids)))
        continue
    minx = min(b.Min.X for b in bbs); maxx = max(b.Max.X for b in bbs)
    miny = min(b.Min.Y for b in bbs); maxy = max(b.Max.Y for b in bbs)
    minz = min(b.Min.Z for b in bbs); maxz = max(b.Max.Z for b in bbs)
    unitish = abs(minz) < 50 and 900 < (maxz - minz) < 1100 and abs(minx + maxx) < 100 and abs(miny + maxy) < 100
    print("  def %s (%-16s): objs=%d bbox=(%.0f,%.0f,%.0f)-(%.0f,%.0f,%.0f) unit-prototype=%s" % (
        did[:8], lp.split('::')[-1], len(ids), minx, miny, minz, maxx, maxy, maxz, unitish))
    checked += 1

# 2) containment audit: axis endpoints vs the instance's own bbox
print("--- axis containment audit ---")
MARGIN = 600.0
total = 0
bad = 0
bad_by_mark = {}
len_mismatch = 0
for obj in model.Objects:
    lp = layers.get(obj.Attributes.LayerIndex, "")
    if "철골" not in lp or type(obj.Geometry).__name__ != "InstanceReference":
        continue
    g = obj.Geometry
    bb = g.GetBoundingBox()
    a = xform_point(g.Xform, (0, 0, 0))
    b = xform_point(g.Xform, (0, 0, 1000.0))
    total += 1
    ok = True
    for p in (a, b):
        if not (bb.Min.X - MARGIN <= p[0] <= bb.Max.X + MARGIN and
                bb.Min.Y - MARGIN <= p[1] <= bb.Max.Y + MARGIN and
                bb.Min.Z - MARGIN <= p[2] <= bb.Max.Z + MARGIN):
            ok = False
    L = math.dist(a, b)
    diag = math.dist((bb.Min.X, bb.Min.Y, bb.Min.Z), (bb.Max.X, bb.Max.Y, bb.Max.Z))
    if L > diag + 100:
        len_mismatch += 1
        ok = False
    if not ok:
        bad += 1
        mark = lp.split("::")[-1]
        bad_by_mark[mark] = bad_by_mark.get(mark, 0) + 1

print("instances audited: %d, axis-outside-bbox or overlong: %d (%.1f%%), overlong: %d" % (
    total, bad, 100.0 * bad / max(total, 1), len_mismatch))
if bad_by_mark:
    for k, v in sorted(bad_by_mark.items(), key=lambda kv: -kv[1]):
        print("  bad %-18s %d" % (k, v))
