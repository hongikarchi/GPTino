# GPTino structural solver asset (structural_solve host tool).
#
# SHIPPED, DETERMINISTIC CODE — the agent never writes or edits this. It is the promotion of the
# live-validated scripts/pynite-real-model.py (1,199-member production model: 0.85 s solve, exact
# equilibrium, 0.9 % cross-validation against Karamba), parameterized for tool use. Reproducibility
# is the point: "the frame was solved correctly" is a safety claim, and a safety claim regenerated
# by a language model on every call is not a claim at all.
#
# Contract: argv[1] = input JSON path, stdout = result JSON, stderr = diagnostics, exit 0 on
# success. Runs on Python 3.9+ (Rhino's py39-rh8 environment) with PyNiteFEA installed.
#
# Input (units mm / kN unless noted):
# {
#   "members":  [{"mark","a":[x,y,z],"b":[x,y,z],"kind","sourceObjectIds":[...]}, ...],
#   "sections": {"H-300x300x10x15": {"H","B","tw","tf","A","Ix","Iy"}, ...}   (KS catalog rows),
#   "markSections": {"SC1": "H-300x300x10x15", ...},
#   "defaultSection": "H-300x300x10x15",
#   "options": {
#     "gridMm": 30, "snapMm": 350, "repairSnapMm": 1500,
#     "repairFreeEnds": false,            # wide-radius repair ONLY after the user said so
#     "cantileverPoints": [[x,y,z],...],  # user-confirmed intended free ends: never repaired
#     "columnMarkPrefixes": ["SC"],
#     "extraDistributedKnPerM": {"SG1": 5.0},   # user-supplied line loads (dead+live lumped, v1)
#     "deflectionLimitRatio": 250          # member check: max local deflection <= L / ratio
#   }
# }
import collections
import json
import math
import sys
import time

from Pynite import FEModel3D

E = 2.1e8        # kN/m2 (S235/S355 steel)
G = 8.076e7
RHO_KNM3 = 78.5  # steel unit weight kN/m3


def torsion_j_cm4(H, B, tw, tf):
    # thin-walled open-section estimate: J = sum(b*t^3)/3 (cm^4); inputs mm
    return ((2.0 * B * tf ** 3) + ((H - 2.0 * tf) * tw ** 3)) / 3.0 / 1e4


def main():
    with open(sys.argv[1], encoding="utf-8") as handle:
        payload = json.load(handle)
    members = payload["members"]
    sections = payload["sections"]
    mark_sections = payload.get("markSections", {})
    default_section = payload.get("defaultSection")
    options = payload.get("options", {})

    grid = float(options.get("gridMm", 30.0))
    snap = float(options.get("snapMm", 350.0))
    repair_snap = float(options.get("repairSnapMm", 1500.0))
    repair_free_ends = bool(options.get("repairFreeEnds", False))
    cantilevers = [tuple(point) for point in options.get("cantileverPoints", [])]
    column_prefixes = tuple(options.get("columnMarkPrefixes", ["SC"]))
    extra_w = options.get("extraDistributedKnPerM", {})
    limit_ratio = float(options.get("deflectionLimitRatio", 250.0))

    def is_confirmed_cantilever(p):
        return any(math.dist(p, c) <= snap for c in cantilevers)

    # ---- node merge pass 1: exact grid --------------------------------------------------
    nodes = {}
    node_xyz = []

    def node_of(p):
        k = (round(p[0] / grid), round(p[1] / grid), round(p[2] / grid))
        if k not in nodes:
            nodes[k] = len(node_xyz)
            node_xyz.append(list(p))
        return nodes[k]

    edges = []
    for index, member in enumerate(members):
        a, b = node_of(member["a"]), node_of(member["b"])
        if a == b:
            continue
        edges.append({"a": a, "b": b, "member": index, "mark": member["mark"]})

    def build_deg():
        deg = collections.Counter()
        for e in edges:
            deg[e["a"]] += 1
            deg[e["b"]] += 1
        return deg

    # ---- pass 2a: endpoint->endpoint snap (beam drawn to a column FACE, not its axis) ---
    snapped = 0
    for _round in range(4):
        deg = build_deg()
        remap = {}
        for ni, xyz in enumerate(node_xyz):
            if deg[ni] == 0 or ni in remap:
                continue
            best, bd = None, snap
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
        vx, vy, vz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        l2 = vx * vx + vy * vy + vz * vz
        if l2 <= 0:
            return None, None
        t = ((p[0] - a[0]) * vx + (p[1] - a[1]) * vy + (p[2] - a[2]) * vz) / l2
        if t < 0.02 or t > 0.98:
            return None, None
        q = (a[0] + t * vx, a[1] + t * vy, a[2] + t * vz)
        return math.dist(p, q), q

    # ---- pass 2b: T-junctions (secondary landing mid-span on a girder) ------------------
    tsplit = 0
    for _round in range(4):
        deg = build_deg()
        ends = [n for n in range(len(node_xyz)) if deg[n] == 1]
        did = False
        for ni in ends:
            p = node_xyz[ni]
            best = None
            for ei, e in enumerate(edges):
                if ni in (e["a"], e["b"]):
                    continue
                d, q = seg_project(p, node_xyz[e["a"]], node_xyz[e["b"]])
                if d is not None and d < snap and (best is None or d < best[0]):
                    best = (d, ei, q)
            if best is None:
                continue
            _, ei, q = best
            host = edges[ei]
            node_xyz[ni] = list(q)
            edges.append({"a": ni, "b": host["b"], "member": host["member"], "mark": host["mark"]})
            host["b"] = ni
            tsplit += 1
            did = True
        if not did:
            break

    # ---- pass 2c: wide-radius repair, ONLY with the user's explicit yes -----------------
    # Repair mutates the ANALYSIS GRAPH (never the Rhino document), but pulling a member's end
    # 1.5 m is still a judgment call: confirmed-cantilever points are exempt, and without
    # repairFreeEnds the surviving free ends are REPORTED for the ask-back instead of fixed.
    repaired = 0
    if repair_free_ends:
        deg = build_deg()
        for ni in [n for n in range(len(node_xyz)) if deg[n] == 1]:
            p = node_xyz[ni]
            if is_confirmed_cantilever(p):
                continue
            best_n, bd = None, repair_snap
            for nj in range(len(node_xyz)):
                if nj == ni or deg[nj] == 0:
                    continue
                d = math.dist(p, node_xyz[nj])
                if 1.0 < d < bd:
                    best_n, bd = nj, d
            if best_n is not None and deg[best_n] > 1:
                for e in edges:
                    if e["a"] == ni:
                        e["a"] = best_n
                    if e["b"] == ni:
                        e["b"] = best_n
                repaired += 1
                continue
            best_e = None
            for ei, e in enumerate(edges):
                if ni in (e["a"], e["b"]):
                    continue
                dq, q = seg_project(p, node_xyz[e["a"]], node_xyz[e["b"]])
                if dq is not None and dq < repair_snap and (best_e is None or dq < best_e[0]):
                    best_e = (dq, ei, q)
            if best_e is not None:
                _, ei, q = best_e
                host = edges[ei]
                node_xyz[ni] = list(q)
                edges.append({"a": ni, "b": host["b"], "member": host["member"], "mark": host["mark"]})
                host["b"] = ni
                repaired += 1

    for e in edges:
        e["len"] = math.dist(node_xyz[e["a"]], node_xyz[e["b"]])
    edges = [e for e in edges if e["a"] != e["b"] and e["len"] > 50.0]

    # ---- connectivity: solve the LARGEST component, report dropped islands --------------
    parent = list(range(len(node_xyz)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for e in edges:
        parent[find(e["a"])] = find(e["b"])
    component = collections.Counter(find(e["a"]) for e in edges)
    if not component:
        print(json.dumps({"error": "no edges after merge"}))
        return 2
    main_root = component.most_common(1)[0][0]
    main_edges = [e for e in edges if find(e["a"]) == main_root]
    island_edges = len(edges) - len(main_edges)

    # Islands are DROPPED from the solve but must never vanish from the report: a member that
    # connects to nothing is exactly the ask-back condition (mis-drawn? belongs to another
    # structure? intended?), and hiding it inside a bare count would bury the question.
    island_members = []
    seen_island = set()
    for e in edges:
        if find(e["a"]) == main_root:
            continue
        member = members[e["member"]]
        key = e["member"]
        if key in seen_island:
            continue
        seen_island.add(key)
        if len(island_members) < 20:
            island_members.append({
                "mark": member["mark"],
                "aMm": [round(v, 1) for v in member["a"]],
                "bMm": [round(v, 1) for v in member["b"]],
                "sourceObjectIds": member.get("sourceObjectIds", []),
            })

    used = sorted({e["a"] for e in main_edges} | {e["b"] for e in main_edges})
    zmin = min(node_xyz[n][2] for n in used)

    # ---- supports: base band + lower ends of near-vertical columns ----------------------
    touch = collections.defaultdict(list)
    for e in main_edges:
        touch[e["a"]].append(e)
        touch[e["b"]].append(e)
    supports = set(n for n in used if node_xyz[n][2] < zmin + 200.0)
    for e in main_edges:
        if not any(e["mark"].startswith(prefix) for prefix in column_prefixes):
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

    # ---- remaining free ends (post-merge truth; the ask-back list) ----------------------
    deg = build_deg()
    free_remaining = []
    for n in used:
        if deg[n] != 1:
            continue
        if n in supports:
            continue
        point = node_xyz[n]
        source = []
        for e in touch[n]:
            source.extend(members[e["member"]].get("sourceObjectIds", []))
        free_remaining.append({
            "xyzMm": [round(v, 1) for v in point],
            "confirmedCantilever": is_confirmed_cantilever(point),
            "sourceObjectIds": sorted(set(source)),
        })

    # ---- PyNite model (meters / kN) -----------------------------------------------------
    fe = FEModel3D()
    fe.add_material("steel", E, G, 0.3, RHO_KNM3)
    for name, s in sections.items():
        # PyNite add_section(name, A, Iy, Iz, J): the Iy argument is the STRONG axis. This is the
        # documented axis-convention trap (cross-validation caught an 18x disagreement when these
        # were swapped) — Ix (strong) goes SECOND, Iy (weak) third. Do not "fix" the order.
        fe.add_section(
            name,
            s["A"] / 1e4,
            s["Ix"] / 1e8,
            s["Iy"] / 1e8,
            torsion_j_cm4(s["H"], s["B"], s["tw"], s["tf"]) / 1e8)

    for n in used:
        x, y, z = node_xyz[n]
        fe.add_node("N%d" % n, x / 1000.0, y / 1000.0, z / 1000.0)
    for n in supports:
        fe.def_support("N%d" % n, True, True, True, True, True, True)

    missing_sections = collections.Counter()
    total_w = 0.0
    for i, e in enumerate(main_edges):
        profile = mark_sections.get(e["mark"]) or default_section
        if profile not in sections:
            missing_sections[e["mark"]] += 1
            profile = default_section
        if profile not in sections:
            print(json.dumps({"error": "no usable section for mark %s" % e["mark"]}))
            return 2
        fe.add_member("M%d" % i, "N%d" % e["a"], "N%d" % e["b"], "steel", profile)
        w = sections[profile]["A"] / 1e4 * RHO_KNM3  # kN/m self weight
        extra = float(extra_w.get(e["mark"], 0.0))
        fe.add_member_dist_load("M%d" % i, "FZ", -(w + extra), -(w + extra), case="SW")
        total_w += (w + extra) * e["len"] / 1000.0

    fe.add_load_combo("SW", {"SW": 1.0})
    t0 = time.time()
    fe.analyze(check_statics=False)
    solve_s = time.time() - t0

    # ---- results ------------------------------------------------------------------------
    disps = []
    for n in used:
        node = fe.nodes["N%d" % n]
        d = float(math.sqrt(node.DX["SW"] ** 2 + node.DY["SW"] ** 2 + node.DZ["SW"] ** 2))
        disps.append((d, n))
    disps.sort(reverse=True)
    max_d, max_n = disps[0] if disps else (0.0, None)
    sum_rz = float(sum(fe.nodes["N%d" % n].RxnFZ["SW"] for n in supports))
    equilibrium_error = abs(sum_rz - total_w) / total_w * 100.0 if total_w > 0 else 0.0

    # Member deflection check: max local transverse deflection vs L / ratio. This is the
    # "where is the problem" verdict — every failed member carries its source object ids so
    # the agent can POINT at the real solids instead of describing coordinates in prose.
    checks = []
    for i, e in enumerate(main_edges):
        member = fe.members["M%d" % i]
        try:
            # float() casts strip numpy scalar types — json.dumps refuses numpy.bool_/float64,
            # and PyNite's deflection API returns numpy scalars.
            dy = float(abs(member.max_deflection("dy", "SW"))) * 1000.0
            dy_min = float(abs(member.min_deflection("dy", "SW"))) * 1000.0
            dz = float(abs(member.max_deflection("dz", "SW"))) * 1000.0
            dz_min = float(abs(member.min_deflection("dz", "SW"))) * 1000.0
        except Exception:
            continue
        worst = max(dy, dy_min, dz, dz_min)
        limit = e["len"] / limit_ratio
        checks.append({
            "mark": e["mark"],
            "lengthMm": round(e["len"], 1),
            "deflectionMm": round(worst, 3),
            "limitMm": round(limit, 3),
            "ratio": round(worst / limit, 3) if limit > 0 else None,
            "passed": bool(worst <= limit),
            "aMm": [round(v, 1) for v in node_xyz[e["a"]]],
            "bMm": [round(v, 1) for v in node_xyz[e["b"]]],
            "sourceObjectIds": members[e["member"]].get("sourceObjectIds", []),
        })
    failed = sorted(
        [c for c in checks if not c["passed"]],
        key=lambda c: -(c["ratio"] or 0.0))[:20]

    viz_nodes = {}
    for n in used:
        node = fe.nodes["N%d" % n]
        viz_nodes[str(n)] = {
            "xyzMm": [round(v, 1) for v in node_xyz[n]],
            "dMm": [round(float(node.DX["SW"]) * 1000.0, 3), round(float(node.DY["SW"]) * 1000.0, 3),
                    round(float(node.DZ["SW"]) * 1000.0, 3)],
        }

    report = {
        "solveSeconds": round(solve_s, 3),
        "membersIn": len(members),
        "edgesSolved": len(main_edges),
        "islandEdgesDropped": island_edges,
        "islandMembers": island_members,
        "nodes": len(used),
        "supports": len(supports),
        "snappedFreeEnds": snapped,
        "tJunctionSplits": tsplit,
        "repairedFreeEnds": repaired,
        "freeEndsRemaining": free_remaining,
        "missingSectionMarks": dict(missing_sections),
        "totalLoadKn": round(total_w, 2),
        "sumReactionsFzKn": round(sum_rz, 2),
        "equilibriumErrorPercent": round(equilibrium_error, 3),
        "maxDisplacementMm": round(max_d * 1000.0, 3),
        "maxDisplacementXyzMm": [round(v, 1) for v in node_xyz[max_n]] if max_n is not None else None,
        "deflectionLimit": "L/%d" % int(limit_ratio),
        "memberChecks": {
            "checked": len(checks),
            "passed": sum(1 for c in checks if c["passed"]),
            "failed": len(checks) - sum(1 for c in checks if c["passed"]),
        },
        "failedMembers": failed,
        "checks": checks,
        "viz": {
            "nodes": viz_nodes,
            "edges": [{"a": e["a"], "b": e["b"], "mark": e["mark"]} for e in main_edges],
        },
    }
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
