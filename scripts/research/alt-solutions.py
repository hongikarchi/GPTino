# Spec steps 2/3/5 on the real model: junction integrity report (accidental-cantilever
# suspects -> questions for the user), worst-member identification in the main zone, and
# solution alternatives (section upsize / added support) solved and exported per-alt for
# visual comparison on the canvas.
import json
import math
import collections
from Pynite import FEModel3D

VIZ = "artifacts/pynite-real-report-viz.json"
SCHED = "artifacts/steel-schedule-260803.json"
KSCAT = "assets/data/structural/sections-ks.json"

viz = json.load(open(VIZ, encoding="utf-8"))
nodes = {int(k): v for k, v in viz["nodes"].items()}
edges = viz["edges"]
sched = {r["mark"]: r["profile"] for r in json.load(open(SCHED, encoding="utf-8"))["schedule"]}
ks = {s["name"]: s for s in json.load(open(KSCAT, encoding="utf-8"))["sections"]}

E, G, RHO = 2.1e8, 8.076e7, 78.5

def build(extra_supports=(), upsized_marks=None):
    fe = FEModel3D()
    fe.add_material("s", E, G, 0.3, RHO)
    upsize = {  # one catalog step up (same family feel, stronger)
        "H-600x200x11x17": "H-700x300x13x24",
        "H-500x200x10x16": "H-600x200x11x17",
        "H-400x200x8x13": "H-500x200x10x16",
        "H-300x150x6.5x9": "H-400x200x8x13",
        "H-582x300x12x17": "H-700x300x13x24",
    }
    for name, s in ks.items():
        J = ((2 * s["B"] * s["tf"] ** 3) + ((s["H"] - 2 * s["tf"]) * s["tw"] ** 3)) / 3.0 / 1e4
        fe.add_section(name, s["A"] / 1e4, s["Ix"] / 1e8, s["Iy"] / 1e8, J / 1e8)
    for n, d in nodes.items():
        x, y, z = d["xyz_mm"]
        fe.add_node("N%d" % n, x / 1000.0, y / 1000.0, z / 1000.0)
    deg = collections.Counter()
    touch = collections.defaultdict(list)
    for e in edges:
        deg[e["a"]] += 1; deg[e["b"]] += 1
        touch[e["a"]].append(e); touch[e["b"]].append(e)
    zmin = min(d["xyz_mm"][2] for d in nodes.values())
    sup = set(n for n, d in nodes.items() if d["xyz_mm"][2] < zmin + 200.0)
    for e in edges:
        if not e["mark"].startswith("SC") or "Bracing" in e["mark"]:
            continue
        az, bz = nodes[e["a"]]["xyz_mm"][2], nodes[e["b"]]["xyz_mm"][2]
        L = math.dist(nodes[e["a"]]["xyz_mm"], nodes[e["b"]]["xyz_mm"])
        if L <= 0 or abs(az - bz) / L < 0.85:
            continue
        lo = e["a"] if az < bz else e["b"]
        lo_z = nodes[lo]["xyz_mm"][2]
        if not any(min(nodes[o["a"]]["xyz_mm"][2], nodes[o["b"]]["xyz_mm"][2]) < lo_z - 100.0
                   for o in touch[lo] if o is not e):
            sup.add(lo)
    sup |= set(extra_supports)
    for n in sup:
        fe.def_support("N%d" % n, True, True, True, True, True, True)
    for i, e in enumerate(edges):
        prof = sched.get(e["mark"], "H-300x300x10x15")
        if upsized_marks and e["mark"] in upsized_marks:
            prof = upsize.get(prof, prof)
        if prof not in ks:
            prof = "H-300x300x10x15"
        fe.add_member("M%d" % i, "N%d" % e["a"], "N%d" % e["b"], "s", prof)
        w = ks[prof]["A"] / 1e4 * RHO
        fe.add_member_dist_load("M%d" % i, "FZ", -w, -w, case="SW")
    fe.add_load_combo("SW", {"SW": 1.0})
    fe.analyze(check_statics=False)
    return fe, sup, deg

fe0, sup0, deg0 = build()

# ---- spec 2: junction integrity / cantilever suspects (deg-1 non-support horizontals)
suspects = []
for n, d in nodes.items():
    if deg0[n] != 1 or n in sup0:
        continue
    e = next(e for e in edges if n in (e["a"], e["b"]))
    if e["mark"].startswith("SC") and "Bracing" not in e["mark"]:
        continue
    suspects.append({"node": n, "mark": e["mark"], "xyz_mm": d["xyz_mm"]})

# ---- worst main-zone node (z < 15 m)
def disp(fe, n):
    nd = fe.nodes["N%d" % n]
    return math.sqrt(nd.DX["SW"] ** 2 + nd.DY["SW"] ** 2 + nd.DZ["SW"] ** 2) * 1000.0

main_nodes = [n for n, d in nodes.items() if d["xyz_mm"][2] < 15000.0]
worst = max(main_nodes, key=lambda n: disp(fe0, n))
worst_d = disp(fe0, worst)
worst_marks = sorted({e["mark"] for e in edges if worst in (e["a"], e["b"])})

# ---- alternatives
alts = {
    "base": (set(), None),
    "alt-upsize": (set(), set(worst_marks)),
    "alt-support": ({worst}, None),
}
results = {}
for name, (xs, um) in alts.items():
    fe, _, _ = build(extra_supports=xs, upsized_marks=um)
    results[name] = {
        "worst_node_disp_mm": round(disp(fe, worst), 2),
        "max_main_zone_mm": round(max(disp(fe, n) for n in main_nodes), 2),
    }
    out = {"note": name, "nodes": {}, "edges": edges}
    for n, d in nodes.items():
        nd = fe.nodes["N%d" % n]
        out["nodes"][n] = {"xyz_mm": d["xyz_mm"],
                           "d_mm": [round(nd.DX["SW"] * 1e3, 3), round(nd.DY["SW"] * 1e3, 3), round(nd.DZ["SW"] * 1e3, 3)]}
    json.dump(out, open("artifacts/viz-%s.json" % name, "w", encoding="utf-8"))

report = {
    "junction_suspects_count": len(suspects),
    "junction_suspects_sample": suspects[:10],
    "worst_main_node": worst,
    "worst_main_xyz": nodes[worst]["xyz_mm"],
    "worst_marks": worst_marks,
    "alternatives": results,
}
json.dump(report, open("artifacts/alt-solutions-report.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(json.dumps(report, ensure_ascii=False, indent=1))
