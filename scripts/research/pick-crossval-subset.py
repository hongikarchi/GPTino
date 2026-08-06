# Cross-validation subset picker: find a clean portal frame in the extracted real-model
# axes (two vertical SC columns whose tops are bridged by one SG/SB girder), solve it in
# PyNite as the oracle, and emit (a) subset JSON (b) a Karamba task brief with exact
# coordinates and the oracle values for the harness expect file.
import json
import math
import sys

from Pynite import FEModel3D

IN = "artifacts/steel-members.json"
OUT = "artifacts/crossval-subset.json"

E = 2.1e8
G = 8.076e7
RHO = 78.5

# KS props (A_cm2, Ix_cm4, Iy_cm4) subset used here; must match pynite-real-model table.
KS = {
    "H-400x400x13x21": (400, 408, 13, 21, 218.7, 66600, 22400),
    "H-350x350x12x19": (350, 350, 12, 19, 173.9, 40300, 13600),
    "H-300x300x10x15": (300, 300, 10, 15, 119.8, 20400, 6750),
    "H-582x300x12x17": (582, 300, 12, 17, 174.5, 103000, 7670),
    "H-600x200x11x17": (600, 200, 11, 17, 134.4, 77600, 2280),
    "H-500x200x10x16": (500, 200, 10, 16, 114.2, 47800, 2140),
    "H-450x200x9x14":  (450, 200, 9, 14, 96.76, 33500, 1870),
    "H-400x200x8x13":  (400, 200, 8, 13, 84.12, 23700, 1740),
}
MARKS = {
    "SC1": "H-400x400x13x21", "SC2": "H-350x350x12x19", "SC3": "H-300x300x10x15",
    "SG1": "H-582x300x12x17", "SG2": "H-582x300x12x17", "SG3": "H-582x300x12x17",
    "SG4": "H-582x300x12x17", "SG5": "H-582x300x12x17", "SG6": "H-600x200x11x17",
    "SG7": "H-500x200x10x16", "SG8": "H-450x200x9x14",
}

data = json.load(open(IN, encoding="utf-8"))
members = [m for m in data["members"] if not m.get("approx")]

cols = [m for m in members if m["mark"].startswith("SC") and m["mark"] in MARKS
        and abs(m["ax"][2] - m["bx"][2]) / max(m["len_mm"], 1) > 0.9]
girders = [m for m in members if m["mark"].startswith("SG") and m["mark"] in MARKS
           and abs(m["ax"][2] - m["bx"][2]) < 200]

def top(c):
    return c["ax"] if c["ax"][2] > c["bx"][2] else c["bx"]
def bot(c):
    return c["ax"] if c["ax"][2] <= c["bx"][2] else c["bx"]

TOL = 400.0
best = None
for g in girders:
    ca = cb = None
    for c in cols:
        t = top(c)
        if math.dist(t, g["ax"]) < TOL:
            ca = c
        if math.dist(t, g["bx"]) < TOL:
            cb = c
    if ca is not None and cb is not None and ca is not cb:
        span = math.dist(g["ax"], g["bx"])
        if best is None or span > best[3]:
            best = (g, ca, cb, span)

if best is None:
    print("no clean portal found")
    sys.exit(1)

g, ca, cb, span = best
print("portal: girder %s span=%.0fmm, columns %s(%.0fmm) %s(%.0fmm)" % (
    g["mark"], span, ca["mark"], ca["len_mm"], cb["mark"], cb["len_mm"]))

# Idealized portal in local coords: column bases at z=0, girder connecting tops.
# Use column top->girder-end coordinates as-is (mm -> m), shift origin to ca base.
o = bot(ca)
def L(p):
    return [round((p[i] - o[i]) / 1000.0, 4) for i in range(3)]

model_geo = {
    "colA": {"base": L(bot(ca)), "top": L(top(ca)), "profile": MARKS[ca["mark"]], "mark": ca["mark"]},
    "colB": {"base": L(bot(cb)), "top": L(top(cb)), "profile": MARKS[cb["mark"]], "mark": cb["mark"]},
    "girder": {"a": L(g["ax"]), "b": L(g["bx"]), "profile": MARKS[g["mark"]], "mark": g["mark"]},
}

# ---- PyNite oracle: fixed bases, rigid joints, self-weight + 10 kN midspan point load
fe = FEModel3D()
fe.add_material("steel", E, G, 0.3, RHO)
for name, (H, B, tw, tf, A, Ix, Iy) in KS.items():
    J = ((2 * B * tf ** 3) + ((H - 2 * tf) * tw ** 3)) / 3.0 / 1e4
    # Pynite convention (live-verified): Iy argument = STRONG axis for vertical bending.
    fe.add_section(name, A / 1e4, Ix / 1e8, Iy / 1e8, J / 1e8)

def NP(tag, p):
    fe.add_node(tag, p[0], p[1], p[2])

NP("A0", model_geo["colA"]["base"]); NP("A1", model_geo["colA"]["top"])
NP("B0", model_geo["colB"]["base"]); NP("B1", model_geo["colB"]["top"])
ga, gb = model_geo["girder"]["a"], model_geo["girder"]["b"]
mid = [(ga[i] + gb[i]) / 2 for i in range(3)]
NP("GM", mid)
fe.def_support("A0", True, True, True, True, True, True)
fe.def_support("B0", True, True, True, True, True, True)

# girder ends coincide with column tops within TOL; connect A1-GM-B1
fe.add_member("colA", "A0", "A1", "steel", model_geo["colA"]["profile"])
fe.add_member("colB", "B0", "B1", "steel", model_geo["colB"]["profile"])
fe.add_member("g1", "A1", "GM", "steel", model_geo["girder"]["profile"])
fe.add_member("g2", "GM", "B1", "steel", model_geo["girder"]["profile"])

for mname in ("colA", "colB", "g1", "g2"):
    prof = fe.members[mname].section.name
    w = KS[prof][4] / 1e4 * RHO
    fe.add_member_dist_load(mname, "FZ", -w, -w, case="SW")
fe.add_node_load("GM", "FZ", -10.0, case="SW")
fe.add_load_combo("SW", {"SW": 1.0})
fe.analyze(check_statics=False)

n = fe.nodes["GM"]
disp_mid = {"dx": n.DX["SW"], "dy": n.DY["SW"], "dz": n.DZ["SW"]}
sum_rz = sum(fe.nodes[s].RxnFZ["SW"] for s in ("A0", "B0"))
total_load = 10.0 + sum(KS[fe.members[m].section.name][4] / 1e4 * RHO * fe.members[m].L() for m in fe.members)

oracle = {
    "midspan_dz_mm": round(disp_mid["dz"] * 1000.0, 3),
    "midspan_d_total_mm": round(math.sqrt(sum(v * v for v in disp_mid.values())) * 1000.0, 3),
    "sum_reactions_FZ_kN": round(sum_rz, 3),
    "total_load_kN": round(total_load, 3),
}
out = {"geometry_m": model_geo, "loading": "self-weight + 10 kN down at girder midspan, bases fixed", "pynite_oracle": oracle}
json.dump(out, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(json.dumps(out, ensure_ascii=False, indent=1))
