using Xunit;
using static GPTino.Rhino.StructuralAxisMath;

namespace GPTino.Rhino.Tests;

/// <summary>
/// The extraction math is a safety claim ("these lines are where the members are"), so its parts
/// are pinned headless: transform recovery, PCA axis, dedupe preference order, free-end
/// detection, and the oblique quality signal. Values mirror the live-validated real-model runs.
/// </summary>
public sealed class StructuralAxisMathTests
{
    private static Vec3 V(double x, double y, double z) => new(x, y, z);

    [Fact]
    public void TransformPointRecoversAScaledTranslatedPrototypeAxis()
    {
        // A 1000mm unit prototype placed at (5000, 2000, 0) and stretched ×3 in Z — the exact
        // pattern of the real model's unit-block members (column of height 3000).
        var matrix = new double[]
        {
            1, 0, 0, 5000,
            0, 1, 0, 2000,
            0, 0, 3, 0,
            0, 0, 0, 1,
        };
        var a = TransformPoint(matrix, V(0, 0, 0));
        var b = TransformPoint(matrix, V(0, 0, 1000));
        Assert.Equal(new Vec3(5000, 2000, 0), a);
        Assert.Equal(new Vec3(5000, 2000, 3000), b);
    }

    [Fact]
    public void PrincipalAxisFindsTheLongDirectionOfASlenderBoxAndRefusesAStockyOne()
    {
        // Eight corners of a 200×200×6000 brace-like box, oblique in plan.
        var slender = new List<Vec3>();
        var stocky = new List<Vec3>();
        foreach (var dx in new[] { -100.0, 100.0 })
        {
            foreach (var dy in new[] { -100.0, 100.0 })
            {
                foreach (var t in new[] { 0.0, 6000.0 })
                {
                    // axis along (1,1,0)/√2
                    var along = t / Math.Sqrt(2);
                    slender.Add(V(along + dx, along + dy, dx - dy));
                    stocky.Add(V(dx, dy, t > 0 ? 150 : -150));
                }
            }
        }
        var axis = PrincipalAxisEndpoints(slender, minimumSpan: 300.0);
        Assert.NotNull(axis);
        Assert.True(axis!.Value.Span > 5500, $"span {axis.Value.Span}");
        var direction = (axis.Value.B - axis.Value.A).Unit();
        Assert.True(Math.Abs(direction.Z) < 0.1, "brace axis should be near-horizontal");

        // A 200×200×300 block has no meaningful axis; guessing one would fabricate a member.
        Assert.Null(PrincipalAxisEndpoints(stocky, minimumSpan: 400.0));
    }

    [Fact]
    public void DedupePrefersExactOverPcaAndMergesOnlyWithinAMark()
    {
        var axes = new[]
        {
            // 0: PCA copy of the brace (loose solid duplicate of the instance)
            new Axis("SB2", V(0, 0, 0), V(6000, 0, 0), 6000, Approximate: true),
            // 1: exact instance-derived axis of the same brace, 50mm off
            new Axis("SB2", V(0, 50, 0), V(6000, 50, 0), 6000, Approximate: false),
            // 2: same position, DIFFERENT mark — a real modeling condition, must survive
            new Axis("SG1", V(0, 25, 0), V(6000, 25, 0), 6000, Approximate: false),
        };
        var (kept, merged) = DedupeAxes(axes);
        Assert.Equal(1, merged);
        Assert.Contains(1, kept);   // the exact axis won
        Assert.DoesNotContain(0, kept);
        Assert.Contains(2, kept);   // cross-mark coincidence is preserved for the human to see
    }

    [Fact]
    public void FreeEndDetectionSeesEndpointJointsInteriorLandingsAndTheDeliberateGap()
    {
        var members = new (Vec3 A, Vec3 B)[]
        {
            (V(0, 0, 0), V(6000, 0, 0)),          // girder
            (V(0, 0, 0), V(0, 0, 3000)),          // column meeting the girder end-to-end
            (V(3000, 100, 0), V(3000, 4000, 0)),  // secondary landing mid-span on the girder (T)
            (V(20000, 0, 0), V(26000, 0, 0)),     // floating member — BOTH ends free
        };
        var free = FindFreeEnds(members, snapDistance: 350.0);
        // girder far end (B), column top (B), secondary far end (B), and both floating ends.
        Assert.Equal(5, free.Count);
        Assert.Equal(2, free.Count(f => f.MemberIndex == 3));
        Assert.DoesNotContain(free, f => f.MemberIndex == 2 && f.End == 0); // the T-landing is connected
    }

    [Fact]
    public void ObliqueCountFlagsSkewedAxesButNotGridOrDeliberateDiagonalMembers()
    {
        var members = new (Vec3 A, Vec3 B)[]
        {
            (V(0, 0, 0), V(6000, 0, 0)),        // on-grid X
            (V(0, 0, 0), V(0, 0, 3000)),        // on-grid Z
            (V(0, 0, 0), V(6000, 200, 0)),      // skewed ~1.9° — inside tolerance, still on-grid
            (V(0, 0, 0), V(4000, 4000, 0)),     // 45° plan diagonal — oblique (deliberate or not,
                                                // the count reports it; prose decides which)
        };
        Assert.Equal(1, CountObliqueAxes(members, toleranceDegrees: 3.0));
    }

    [Fact]
    public void MarkPrefixDropsTheVariantSuffix()
    {
        Assert.Equal("SB2", MarkPrefix("SB2 (2)"));
        Assert.Equal("SC1", MarkPrefix("SC1"));
    }
}
