# Map the pipeline's clarification questions to REAL Rhino object GUIDs so the /focus
# endpoint can isolate+zoom them in the viewport (native ask-back UX).
import json
import math
import rhino3dm

PATH = r"C:\Users\user\Desktop\GPTino\260803 main ms.3dm"
model = rhino3dm.File3dm.Read(PATH)
layers = {l.Index: l.FullPath for l in model.Layers}

rep = json.load(open("artifacts/alt-solutions-report.json", encoding="utf-8"))
suspects = rep["junction_suspects_sample"]
worst_xyz = rep["worst_main_xyz"]

steel = []
for obj in model.Objects:
    lp = layers.get(obj.Attributes.LayerIndex, "")
    if "철골" not in lp:
        continue
    bb = obj.Geometry.GetBoundingBox()
    if abs(bb.Min.X) < 5000 and abs(bb.Min.Y) < 5000:
        continue  # prototypes
    steel.append((str(obj.Attributes.Id), lp.split("::")[-1], bb))

def near(pt, pad):
    out = []
    for oid, mark, bb in steel:
        if (bb.Min.X - pad <= pt[0] <= bb.Max.X + pad and
                bb.Min.Y - pad <= pt[1] <= bb.Max.Y + pad and
                bb.Min.Z - pad <= pt[2] <= bb.Max.Z + pad):
            cx = ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, (bb.Min.Z + bb.Max.Z) / 2)
            out.append((math.dist(pt, cx), oid, mark))
    out.sort()
    return out

targets = {}
for i, s in enumerate(suspects):
    hits = near(s["xyz_mm"], 300)[:3]
    targets["Q%d" % (1 + i)] = {
        "label": "자유단 의심: %s @ z=%.0f" % (s["mark"], s["xyz_mm"][2]),
        "ids": [h[1] for h in hits],
        "marks": [h[2] for h in hits],
    }
hits = near(worst_xyz, 500)[:5]
targets["WORST"] = {
    "label": "본관 최악 변위 지점 (SB8/SG7 접합 @ z=%.0f)" % worst_xyz[2],
    "ids": [h[1] for h in hits],
    "marks": [h[2] for h in hits],
}
json.dump(targets, open("artifacts/focus-targets.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
for k, v in targets.items():
    print(k, v["label"], "->", len(v["ids"]), "objects", v["marks"])
