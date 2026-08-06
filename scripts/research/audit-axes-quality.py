# Common-sense quality audit of extracted axes: a real building frame is orthogonal
# grids plus INTENTIONAL diagonals. Quantify everything that violates that expectation:
#   - duplicate axes (same member extracted twice: instance + loose brep)
#   - skewed members that should be vertical (SC columns) or horizontal (SG/SB beams)
#   - near-horizontal beams with slight slopes (drainage vs artifact)
#   - overlapping collinear axes
import json
import math
import collections

d = json.load(open("artifacts/steel-members.json", encoding="utf-8"))
members = d["members"]

def unit(m):
    a, b = m["ax"], m["bx"]
    L = max(m["len_mm"], 1e-6)
    return [(b[i] - a[i]) / L for i in range(3)]

# --- classification per mark
cls = collections.defaultdict(collections.Counter)
slope_examples = collections.defaultdict(list)
for m in members:
    u = unit(m)
    vz = abs(u[2])
    kind = "vertical" if vz > 0.95 else ("horizontal" if vz < 0.02 else
           ("sloped<5%" if vz < 0.05 else "skew"))
    tag = m["mark"] + (" [PCA]" if m.get("approx") else "")
    cls[tag][kind] += 1
    if kind in ("sloped<5%", "skew") and len(slope_examples[tag]) < 2:
        slope_examples[tag].append((round(math.degrees(math.asin(min(vz, 1.0))), 1), round(m["len_mm"])))

print("--- direction census (vertical / horizontal / sloped<5% / skew) ---")
for tag in sorted(cls):
    c = cls[tag]
    total = sum(c.values())
    flag = ""
    if tag.startswith("SC") and "Bracing" not in tag and c["skew"] + c["sloped<5%"] > 0 and "[PCA]" not in tag:
        flag = "  <-- columns should be vertical!"
    if (tag.startswith("SG") or tag.startswith("SB")) and c["skew"] > 0:
        flag = "  <-- beams skewed!"
    print("%-22s n=%-4d V=%-4d H=%-4d slope=%-3d skew=%-4d %s" % (
        tag, total, c["vertical"], c["horizontal"], c["sloped<5%"], c["skew"], flag))

# --- duplicates: endpoint pairs within tolerance (either orientation)
TOL = 150.0
def key(p):
    return (round(p[0] / TOL), round(p[1] / TOL), round(p[2] / TOL))
buckets = collections.defaultdict(list)
for i, m in enumerate(members):
    buckets[(key(m["ax"]), key(m["bx"]))].append(i)
    buckets[(key(m["bx"]), key(m["ax"]))].append(i)
dups = set()
for v in buckets.values():
    s = set(v)
    if len(s) > 1:
        dups.add(frozenset(s))
dup_pairs = [tuple(sorted(fs)) for fs in dups if len(fs) > 1]
dup_marks = collections.Counter()
for pair in dup_pairs:
    marks = {members[i]["mark"] + ("[PCA]" if members[i].get("approx") else "") for i in pair}
    dup_marks[" + ".join(sorted(marks))] += 1
print("\n--- duplicate axes (both endpoints within %dmm) ---" % TOL)
print("duplicate groups:", len(dup_pairs))
for k, v in dup_marks.most_common(10):
    print("  %3d x %s" % (v, k))

# --- PCA brace sanity: braces should connect frame nodes; report length stats + skew angles
angles = []
for m in members:
    if not m.get("approx"):
        continue
    u = unit(m)
    angles.append(round(math.degrees(math.asin(min(abs(u[2]), 1.0)))))
if angles:
    c = collections.Counter(angles)
    print("\n--- PCA member inclination histogram (deg above horizontal) ---")
    for band in range(0, 91, 10):
        n = sum(v for k, v in c.items() if band <= k < band + 10)
        print("  %2d-%2d deg: %s" % (band, band + 9, "#" * min(n // 5 + (1 if n else 0), 60), ), n if False else "")
    import statistics
    print("  n=%d, median=%d deg" % (len(angles), statistics.median(angles)))
