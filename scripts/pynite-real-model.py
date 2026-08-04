# Real-world feasibility: build and solve the extracted steel frame with PyNite (open source).
# Input: artifacts/steel-members.json from extract-steel-axes.py (axes in mm, marks per member).
# Section identity: prototype outer dims are KS nominal x 1.02 -> mapped to KS D 3502 profiles.
# Honest v1 scope: braces skipped upstream, self-weight loading only, rigid joints, two-pass
# node merge (exact grid, then snap of free ends to nearest node within SNAP_MM).
import json
import math
import sys
import time
import collections

from Pynite import FEModel3D

IN = sys.argv[1] if len(sys.argv) > 1 else "artifacts/steel-members.json"
OUT = sys.argv[2] if len(sys.argv) > 2 else "artifacts/pynite-real-report.json"

GRID_MM = 30.0      # exact-merge tolerance
SNAP_MM = 350.0     # free-end snap radius (beam drawn to column face, not axis)
E = 2.1e8           # kN/m2
G = 8.076e7
RHO_KNM3 = 78.5     # steel unit weight kN/m3

# KS D 3502 core table: name: (H,B,tw,tf, A_cm2, Ix_cm4, Iy_cm4)
KS = {
    "H-400x400x13x21": (400, 408, 13, 21, 218.7, 66600, 22400),
    "H-350x350x12x19": (350, 350, 12, 19, 173.9, 40300, 13600),
    "H-300x300x10x15": (300, 300, 10, 15, 119.8, 20400, 6750),
    "H-250x250x9x14":  (250, 250, 9, 14, 92.18, 10800, 3650),
    "H-200x200x8x12":  (200, 200, 8, 12, 63.53, 4720, 1600),
    "H-582x300x12x17": (582, 300, 12, 17, 174.5, 103000, 7670),
    "H-600x200x11x17": (600, 200, 11, 17, 134.4, 77600, 2280),
    "H-500x200x10x16": (500, 200, 10, 16, 114.2, 47800, 2140),
    "H-450x200x9x14":  (450, 200, 9, 14, 96.76, 33500, 1870),
    "H-400x200x8x13":  (400, 200, 8, 13, 84.12, 23700, 1740),
    "H-350x175x7x11":  (350, 175, 7, 11, 63.14, 13600, 984),
    "H-300x150x6.5x9": (300, 150, 6.5, 9, 46.78, 7210, 508),
    "H-194x150x6x9":   (194, 150, 6, 9, 39.01, 2690, 507),
    "H-900x300x16x28": (900, 300, 16, 28, 309.8, 411000, 12600),
    "H-800x300x14x26": (800, 300, 14, 26, 267.4, 292000, 11700),
    "H-700x300x13x24": (700, 300, 13, 24, 235.5, 201000, 10800),
    "H-390x300x10x16": (390, 300, 10, 16, 136.0, 38700, 7210),
    "H-340x250x9x14":  (340, 250, 9, 14, 101.5, 21700, 3650),
    "H-294x200x8x12":  (294, 200, 8, 12, 72.38, 11300, 1600),
    "H-414x405x18x28": (414, 405, 18, 28, 295.4, 92800, 31000),
    "H-428x407x20x35": (428, 407, 20, 35, 360.7, 119000, 39400),
}

def nearest_profile(bx, by):
    depth = max(bx, by) / 1.02
    width = min(bx, by) / 1.02
    best, err = None, 1e9
    for name, (H, B, *_rest) in KS.items():
        e = abs(H - depth) + abs(B - width)
        if e < err:
            best, err = name, e
    return best, err

def torsion_J_cm4(H, B, tw, tf):
    # thin-walled open-section estimate: J = sum(b t^3)/3 (cm^4); inputs mm
    return ((2 * B * tf ** 3) + ((H - 2 * tf) * tw ** 3)) / 3.0 / 1e4

data = json.load(open(IN, encoding="utf-8"))
members = data["members"]
protos = data["summary"]["prototype_outer_dims_mm"]

mark_profile = {}
for lp, d in protos.items():
    mark = lp.split("::")[-1]
    name, err = nearest_profile(d["bx"], d["by"])
    mark_profile[mark] = (name, round(err, 1))

# ---- node merge pass 1: exact grid
def key(p):
    return (round(p[0] / GRID_MM), round(p[1] / GRID_MM), round(p[2] / GRID_MM))

nodes = {}
node_xyz = []
def node_of(p):
    k = key(p)
    if k not in nodes:
        nodes[k] = len(node_xyz)
        node_xyz.append(list(p))
    return nodes[k]

edges = []
for m in members:
    a, b = node_of(m["ax"]), node_of(m["bx"])
    if a == b:
        continue
    edges.append({"a": a, "b": b, "mark": m["mark"], "len": m["len_mm"]})

# ---- pass 2: real-world joint reconstruction.
# (a) endpoint -> endpoint snap (beam drawn to column FACE, not axis): merge any node pair
#     within SNAP_MM, smaller-degree node moves onto larger-degree node, iterated.
# (b) endpoint -> member-interior projection (secondary beam landing MID-SPAN on a girder):
#     split the carrying member at the projected point (T-junction), iterated to fixpoint.
def build_deg():
    d = collections.Counter()
    for e in edges:
        d[e["a"]] += 1
        d[e["b"]] += 1
    return d

snapped = 0
for _round in range(4):
    deg = build_deg()
    remap = {}
    for ni, xyz in enumerate(node_xyz):
        if deg[ni] == 0 or ni in remap:
            continue
        best, bd = None, SNAP_MM
        for nj, other in enumerate(node_xyz):
            if nj == ni or nj in remap or deg[nj] == 0:
                continue
            d = math.dist(xyz, other)
            if 1.0 < d < bd:
                best, bd = nj, d
        if best is not None and (deg[best], -best) >= (deg[ni], -ni):
            remap[ni] = best
            snapped += 1
    if not remap:
        break
    for e in edges:
        e["a"] = remap.get(e["a"], e["a"])
        e["b"] = remap.get(e["b"], e["b"])
    edges = [e for e in edges if e["a"] != e["b"]]

def seg_project(p, a, b):
    ax, ay, az = a; bx, by, bz = b
    vx, vy, vz = bx-ax, by-ay, bz-az
    L2 = vx*vx + vy*vy + vz*vz
    if L2 <= 0:
        return None, None
    t = ((p[0]-ax)*vx + (p[1]-ay)*vy + (p[2]-az)*vz) / L2
    if t < 0.02 or t > 0.98:
        return None, None
    q = (ax + t*vx, ay + t*vy, az + t*vz)
    return math.dist(p, q), q

tsplit = 0
for _round in range(4):
    deg = build_deg()
    ends = [n for n in range(len(node_xyz)) if deg[n] == 1]
    did = False
    for ni in ends:
        p = node_xyz[ni]
        best = None  # (dist, edge_idx, q)
        for ei, e in enumerate(edges):
            if ni in (e["a"], e["b"]):
                continue
            d, q = seg_project(p, node_xyz[e["a"]], node_xyz[e["b"]])
            if d is not None and d < SNAP_MM and (best is None or d < best[0]):
                best = (d, ei, q)
        if best is None:
            continue
        _, ei, q = best
        host = edges[ei]
        node_xyz[ni] = list(q)  # pull the free end onto the host axis
        edges.append({"a": ni, "b": host["b"], "mark": host["mark"], "len": None})
        host["b"] = ni  # split host at the landed node
        tsplit += 1
        did = True
    if not did:
        break

for e in edges:
    e["len"] = math.dist(node_xyz[e["a"]], node_xyz[e["b"]])
edges = [e for e in edges if e["a"] != e["b"] and e["len"] > 50.0]

# ---- connectivity (union-find)
parent = list(range(len(node_xyz)))
def find(x):
    while parent[x] != x:
        parent[x] = parent[parent[x]]
        x = parent[x]
    return x
def union(x, y):
    parent[find(x)] = find(y)
for e in edges:
    union(e["a"], e["b"])
comp = collections.Counter(find(e["a"]) for e in edges)
main_root = comp.most_common(1)[0][0]
main_edges = [e for e in edges if find(e["a"]) == main_root]
island_edges = len(edges) - len(main_edges)

used_nodes = sorted({e["a"] for e in main_edges} | {e["b"] for e in main_edges})
zmin = min(node_xyz[n][2] for n in used_nodes)

# Supports: the steel sits partly on foundations and partly on a concrete podium that is
# NOT in the steel layers. Rule: the lower node of every near-vertical column member is a
# base support unless some other member hangs on below it (within 100mm).
touch = collections.defaultdict(list)
for e in main_edges:
    touch[e["a"]].append(e)
    touch[e["b"]].append(e)
supports = set(n for n in used_nodes if node_xyz[n][2] < zmin + 200.0)
for e in main_edges:
    if not e["mark"].startswith("SC"):
        continue
    az, bz = node_xyz[e["a"]][2], node_xyz[e["b"]][2]
    if e["len"] <= 0 or abs(az - bz) / e["len"] < 0.85:
        continue
    lo = e["a"] if az < bz else e["b"]
    lo_z = node_xyz[lo][2]
    hangs_below = any(
        min(node_xyz[o["a"]][2], node_xyz[o["b"]][2]) < lo_z - 100.0
        for o in touch[lo] if o is not e)
    if not hangs_below:
        supports.add(lo)
supports = sorted(supports)

# ---- PyNite model (meters / kN)
fe = FEModel3D()
fe.add_material("steel", E, G, 0.3, RHO_KNM3)
sections_added = set()
for name, (H, B, tw, tf, A, Ix, Iy) in KS.items():
    # Pynite add_section(A, Iy, Iz, J): Iy = STRONG axis (governs vertical bending in the
    # default member orientation) — verified live against the 7.62mm beam oracle both for
    # X- and Y-running members; the swapped order under-stiffens vertical bending 4x.
    fe.add_section(name, A / 1e4, Ix / 1e8, Iy / 1e8, torsion_J_cm4(H, B, tw, tf) / 1e8)

for n in used_nodes:
    x, y, z = node_xyz[n]
    fe.add_node("N%d" % n, x / 1000.0, y / 1000.0, z / 1000.0)
for n in supports:
    fe.def_support("N%d" % n, True, True, True, True, True, True)

mark_missing = collections.Counter()
for i, e in enumerate(main_edges):
    prof = mark_profile.get(e["mark"], (None, None))[0]
    if prof is None:
        mark_missing[e["mark"]] += 1
        prof = "H-300x300x10x15"
    fe.add_member("M%d" % i, "N%d" % e["a"], "N%d" % e["b"], "steel", prof)
    A_m2 = KS[prof][4] / 1e4
    w = A_m2 * RHO_KNM3  # kN/m self weight
    fe.add_member_dist_load("M%d" % i, "FZ", -w, -w, case="SW")

fe.add_load_combo("SW", {"SW": 1.0})

t0 = time.time()
fe.analyze(check_statics=False)
solve_s = time.time() - t0

# ---- results
disps = []
for n in used_nodes:
    node = fe.nodes["N%d" % n]
    d = math.sqrt(node.DX["SW"] ** 2 + node.DY["SW"] ** 2 + node.DZ["SW"] ** 2)
    disps.append((d, n))
disps.sort(reverse=True)
maxd, maxn = disps[0] if disps else (0.0, None)
deg_final = collections.Counter()
for e in main_edges:
    deg_final[e["a"]] += 1
    deg_final[e["b"]] += 1
top10 = [
    {"node": "N%d" % n, "d_mm": round(d * 1000.0, 2), "deg": deg_final[n],
     "xyz": [round(v) for v in node_xyz[n]]}
    for d, n in disps[:10]
]
# separate the known flexible canopy chain from the main building verdict
main_zone = [(d, n) for d, n in disps if node_xyz[n][2] < 15000.0]
max_main = main_zone[0] if main_zone else (0.0, None)
hot = {n for d, n in disps[:10]}
chain_marks = collections.Counter(
    e["mark"] for e in main_edges if e["a"] in hot or e["b"] in hot)

total_w = sum(KS[mark_profile.get(e["mark"], ("H-300x300x10x15",))[0] if mark_profile.get(e["mark"]) else "H-300x300x10x15"][4] / 1e4 * RHO_KNM3 * e["len"] / 1000.0 for e in main_edges)
sum_rz = sum(fe.nodes["N%d" % n].RxnFZ["SW"] for n in supports)

report = {
    "members_in": len(members),
    "edges_after_merge": len(edges),
    "main_component_edges": len(main_edges),
    "island_edges_dropped": island_edges,
    "nodes": len(used_nodes),
    "snapped_free_ends": snapped,
    "supports_fixed": len(supports),
    "zmin_mm": zmin,
    "mark_profile_map": {k: v[0] for k, v in sorted(mark_profile.items())},
    "profile_match_err_mm": {k: v[1] for k, v in sorted(mark_profile.items())},
    "marks_without_prototype": dict(mark_missing),
    "solve_seconds": round(solve_s, 2),
    "total_self_weight_kN": round(total_w, 1),
    "sum_support_reactions_FZ_kN": round(sum_rz, 1),
    "max_displacement_mm": round(maxd * 1000.0, 2),
    "max_displacement_node": "N%d" % maxn if maxn is not None else None,
    "max_displacement_xyz_mm": node_xyz[maxn] if maxn is not None else None,
    "t_junction_splits": tsplit,
    "top10_displacements": top10,
    "max_disp_below_z15000_mm": round(max_main[0] * 1000.0, 2),
    "max_disp_below_z15000_node": "N%d" % max_main[1] if max_main[1] is not None else None,
    "max_disp_below_z15000_xyz": [round(v) for v in node_xyz[max_main[1]]] if max_main[1] is not None else None,
    "hot_chain_member_marks": dict(chain_marks),
}
json.dump(report, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(json.dumps(report, ensure_ascii=False, indent=1))
