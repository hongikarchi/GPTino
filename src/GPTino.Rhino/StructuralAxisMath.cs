namespace GPTino.Rhino;

/// <summary>
/// Pure geometry math for structural axis extraction, deliberately free of RhinoCommon types so
/// the safety-relevant parts (dedupe, free-end detection, PCA) are unit-testable without a Rhino
/// process. The adapter converts to and from RhinoCommon at its boundary. Ported from the
/// live-validated scripts/extract-steel-axes.py (1,199-member real model, 557/557 containment
/// audit) — thresholds keep the validated values as defaults.
/// </summary>
internal static class StructuralAxisMath
{
    internal readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;
        public Vec3 Unit()
        {
            var length = Length;
            return length <= 0 ? new Vec3(0, 0, 0) : new Vec3(X / length, Y / length, Z / length);
        }
    }

    /// <summary>One extracted axis before dedupe. MarkPrefix groups mark variants ("SB2 (2)" → "SB2").</summary>
    internal readonly record struct Axis(string MarkPrefix, Vec3 A, Vec3 B, double Length, bool Approximate);

    /// <summary>Applies a row-major 4x4 affine transform (16 values) to a point.</summary>
    internal static Vec3 TransformPoint(IReadOnlyList<double> m, Vec3 p)
    {
        var x = m[0] * p.X + m[1] * p.Y + m[2] * p.Z + m[3];
        var y = m[4] * p.X + m[5] * p.Y + m[6] * p.Z + m[7];
        var z = m[8] * p.X + m[9] * p.Y + m[10] * p.Z + m[11];
        var w = m[12] * p.X + m[13] * p.Y + m[14] * p.Z + m[15];
        return w is 0.0 or 1.0 ? new Vec3(x, y, z) : new Vec3(x / w, y / w, z / w);
    }

    /// <summary>
    /// Dominant principal axis of a vertex cloud by power iteration over the covariance matrix,
    /// with endpoints at the extreme projections. Returns null when the cloud is too small or the
    /// span is below <paramref name="minimumSpan"/> — a stocky solid has no meaningful axis and
    /// guessing one would put a fictitious member into the analysis model.
    /// </summary>
    internal static (Vec3 A, Vec3 B, double Span)? PrincipalAxisEndpoints(
        IReadOnlyList<Vec3> vertices,
        double minimumSpan)
    {
        if (vertices.Count < 4)
        {
            return null;
        }
        double cx = 0, cy = 0, cz = 0;
        foreach (var v in vertices)
        {
            cx += v.X; cy += v.Y; cz += v.Z;
        }
        var n = (double)vertices.Count;
        cx /= n; cy /= n; cz /= n;
        double sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
        foreach (var v in vertices)
        {
            double dx = v.X - cx, dy = v.Y - cy, dz = v.Z - cz;
            sxx += dx * dx; syy += dy * dy; szz += dz * dz;
            sxy += dx * dy; sxz += dx * dz; syz += dy * dz;
        }
        double ex = 1, ey = 1, ez = 1;
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var nx = sxx * ex + sxy * ey + sxz * ez;
            var ny = sxy * ex + syy * ey + syz * ez;
            var nz = sxz * ex + syz * ey + szz * ez;
            var magnitude = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (magnitude == 0)
            {
                break;
            }
            ex = nx / magnitude; ey = ny / magnitude; ez = nz / magnitude;
        }
        double t0 = double.MaxValue, t1 = double.MinValue;
        foreach (var v in vertices)
        {
            var t = (v.X - cx) * ex + (v.Y - cy) * ey + (v.Z - cz) * ez;
            t0 = Math.Min(t0, t);
            t1 = Math.Max(t1, t);
        }
        if (t1 - t0 < minimumSpan)
        {
            return null;
        }
        var center = new Vec3(cx, cy, cz);
        var direction = new Vec3(ex, ey, ez);
        return (center + direction * t0, center + direction * t1, t1 - t0);
    }

    /// <summary>
    /// Merges near-identical axes. Real assemblies model one member as several solids (main member
    /// + cover plates) and some braces exist both as instances and loose solids — each yields an
    /// almost-coincident axis, and feeding both to the solver doubles that member's stiffness.
    /// Preference order inside a duplicate group: exact (instance/curve) over PCA, then longer over
    /// shorter. Returns the kept indices IN THAT preference order and how many were merged away.
    /// Only axes sharing a mark prefix merge — coincident members of DIFFERENT marks are a real
    /// modeling condition the human should see, not something to silently collapse.
    /// </summary>
    internal static (IReadOnlyList<int> KeptIndices, int MergedAway) DedupeAxes(
        IReadOnlyList<Axis> axes,
        double maximumAngleDegrees = 3.0,
        double maximumMidpointDistance = 250.0)
    {
        var minimumDot = Math.Cos(maximumAngleDegrees * Math.PI / 180.0);
        var order = Enumerable.Range(0, axes.Count)
            .OrderBy(i => axes[i].Approximate)
            .ThenByDescending(i => axes[i].Length)
            .ThenBy(i => i)
            .ToArray();
        var kept = new List<int>();
        var merged = 0;
        foreach (var index in order)
        {
            var axis = axes[index];
            var unit = (axis.B - axis.A).Unit();
            var mid = (axis.A + axis.B) * 0.5;
            var duplicate = false;
            foreach (var keptIndex in kept)
            {
                var other = axes[keptIndex];
                if (!string.Equals(other.MarkPrefix, axis.MarkPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                if (Math.Abs(unit.Dot((other.B - other.A).Unit())) < minimumDot)
                {
                    continue;
                }
                var otherMid = (other.A + other.B) * 0.5;
                if ((mid - otherMid).Length < maximumMidpointDistance)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                merged++;
            }
            else
            {
                kept.Add(index);
            }
        }
        return (kept, merged);
    }

    /// <summary>One unconnected member endpoint: which member, which end (0=A, 1=B), and where.</summary>
    internal readonly record struct FreeEnd(int MemberIndex, int End, Vec3 Point);

    /// <summary>
    /// Endpoints that connect to NOTHING — no other member's endpoint and no other member's
    /// interior — within <paramref name="snapDistance"/>. These are the ask-back items: an
    /// unconnected end is either an intended cantilever or a modeling error, and only the human
    /// knows which. Interior projections use the same 2%-98% window as the solver's T-junction
    /// pass so extraction reports the same free ends the solver would see.
    /// </summary>
    internal static IReadOnlyList<FreeEnd> FindFreeEnds(
        IReadOnlyList<(Vec3 A, Vec3 B)> members,
        double snapDistance = 350.0)
    {
        var free = new List<FreeEnd>();
        for (var i = 0; i < members.Count; i++)
        {
            foreach (var (point, end) in new[] { (members[i].A, 0), (members[i].B, 1) })
            {
                var connected = false;
                for (var j = 0; j < members.Count && !connected; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }
                    var (a, b) = members[j];
                    if ((point - a).Length <= snapDistance || (point - b).Length <= snapDistance)
                    {
                        connected = true;
                        break;
                    }
                    var direction = b - a;
                    var lengthSquared = direction.Dot(direction);
                    if (lengthSquared <= 0)
                    {
                        continue;
                    }
                    var t = (point - a).Dot(direction) / lengthSquared;
                    if (t is < 0.02 or > 0.98)
                    {
                        continue;
                    }
                    var projection = a + direction * t;
                    if ((point - projection).Length <= snapDistance)
                    {
                        connected = true;
                    }
                }
                if (!connected)
                {
                    free.Add(new FreeEnd(i, end, point));
                }
            }
        }
        return free;
    }

    /// <summary>
    /// Members whose direction is more than <paramref name="toleranceDegrees"/> away from every
    /// world axis (±X/±Y/±Z). Buildings are orthogonal grids plus deliberate diagonals, so this
    /// count is the quality signal the acceptance spec asks for: a high oblique share among
    /// EXACT (non-PCA) axes means the extraction is skewed, not that the building is.
    /// </summary>
    internal static int CountObliqueAxes(
        IReadOnlyList<(Vec3 A, Vec3 B)> members,
        double toleranceDegrees = 3.0)
    {
        var minimumDot = Math.Cos(toleranceDegrees * Math.PI / 180.0);
        var count = 0;
        foreach (var (a, b) in members)
        {
            var unit = (b - a).Unit();
            var aligned =
                Math.Abs(unit.X) >= minimumDot ||
                Math.Abs(unit.Y) >= minimumDot ||
                Math.Abs(unit.Z) >= minimumDot;
            if (!aligned)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>"SB2 (2)" → "SB2"; the leading token is the section mark, the rest is a variant.</summary>
    internal static string MarkPrefix(string mark)
    {
        var space = mark.IndexOf(' ');
        return space < 0 ? mark : mark[..space];
    }
}
