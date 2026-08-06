using GPTino.AgentHost.Runtime;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Pure-function tests for the deterministic tidy layout: layered left→right dataflow, real-bounds
/// spacing (no overlap), crossing reduction, group contiguity, connected-cluster scoping, and
/// idempotency (re-tidying an already-tidy graph is a no-op).
/// </summary>
public sealed class CanvasLayoutTests
{
    private const float W = 100f;
    private const float H = 40f;

    private static CanvasObjectState Obj(Guid id, float x, float y, float w = W, float h = H) =>
        new(id, Guid.NewGuid(), "n", new CanvasPoint(x, y), new CanvasSize(w, h), "fp")
        {
            BoundsOrigin = new CanvasPoint(x - w / 2f, y - h / 2f),
        };

    private static WireState Wire(Guid source, Guid target) =>
        new(source, Guid.NewGuid(), target, Guid.NewGuid());

    private static CanvasSnapshot Snapshot(
        IReadOnlyList<CanvasObjectState> objects,
        IReadOnlyList<WireState>? wires = null,
        IReadOnlyList<GroupState>? groups = null) =>
        new(Guid.NewGuid(), "doc", objects, wires ?? Array.Empty<WireState>(), groups ?? Array.Empty<GroupState>());

    // Applies computed moves back onto the snapshot (pivot + bounds-origin shift) so idempotency can be
    // checked the way the live pipeline would see it after a canvas.move commits.
    private static CanvasSnapshot ApplyMoves(CanvasSnapshot canvas, IReadOnlyDictionary<Guid, CanvasPoint> moves)
    {
        var objects = canvas.Objects.Select(o =>
        {
            if (!moves.TryGetValue(o.ObjectId, out var pivot))
            {
                return o;
            }
            var dx = pivot.X - o.Pivot.X;
            var dy = pivot.Y - o.Pivot.Y;
            return o with
            {
                Pivot = pivot,
                BoundsOrigin = o.BoundsOrigin is { } b ? new CanvasPoint(b.X + dx, b.Y + dy) : null,
            };
        }).ToList();
        return canvas with { Objects = objects };
    }

    // Resolved position: the engine omits no-op moves, so a node absent from the result stayed put.
    private static CanvasPoint Pos(CanvasSnapshot canvas, IReadOnlyDictionary<Guid, CanvasPoint> moves, Guid id) =>
        moves.TryGetValue(id, out var p) ? p : canvas.Objects.First(o => o.ObjectId == id).Pivot;

    [Fact]
    public void LinearChainFlowsLeftToRight()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        // All piled at the origin so every node must move; wires a -> b -> c.
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 5, 5), Obj(c, 10, 10)],
            [Wire(a, b), Wire(b, c)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { b });

        Assert.True(Pos(canvas, moves, a).X < Pos(canvas, moves, b).X, "input must be left of script");
        Assert.True(Pos(canvas, moves, b).X < Pos(canvas, moves, c).X, "script must be left of output");
    }

    [Fact]
    public void ParallelInputsShareTheLeftColumnAndDoNotOverlap()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var script = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(s1, 0, 0), Obj(s2, 0, 0), Obj(script, 0, 0)],
            [Wire(s1, script), Wire(s2, script)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        // Two sliders share one column (equal X), the script sits to their right.
        Assert.Equal(Pos(canvas, moves, s1).X, Pos(canvas, moves, s2).X, 3);
        Assert.True(Pos(canvas, moves, script).X > Pos(canvas, moves, s1).X);
        // Column members do not overlap vertically (centers at least one node-height apart).
        Assert.True(Math.Abs(Pos(canvas, moves, s1).Y - Pos(canvas, moves, s2).Y) >= H);
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var canvas = Snapshot([Obj(a, 3, 7), Obj(b, 1, 2)], [Wire(a, b)]);

        var first = CanvasLayout.Arrange(canvas, new[] { a });
        var second = CanvasLayout.Arrange(canvas, new[] { a });

        Assert.Equal(first.Count, second.Count);
        foreach (var (id, pivot) in first)
        {
            Assert.Equal(pivot.X, second[id].X, 4);
            Assert.Equal(pivot.Y, second[id].Y, 4);
        }
    }

    [Fact]
    public void ReTidyingAnAlreadyTidyGraphIsANoOp()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 5, 40), Obj(c, 12, 80)],
            [Wire(a, b), Wire(b, c)]);

        var first = CanvasLayout.Arrange(canvas, new[] { a });
        var tidied = ApplyMoves(canvas, first);
        var second = CanvasLayout.Arrange(tidied, new[] { a });

        Assert.Empty(second);
    }

    [Fact]
    public void OnlyMovesTheClusterContainingTheSeed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var lonelyUserNode = Guid.NewGuid(); // wired to nothing — a separate hand-built node
        var canvas = Snapshot(
            [Obj(a, 0, 0), Obj(b, 0, 0), Obj(lonelyUserNode, 500, 500)],
            [Wire(a, b)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { a });

        Assert.False(moves.ContainsKey(lonelyUserNode));
        Assert.True(moves.ContainsKey(a) || moves.ContainsKey(b));
    }

    [Fact]
    public void RelatedUserNodesAreIncludedInTheCluster()
    {
        var userSlider = Guid.NewGuid();   // user placed it; agent wired it in
        var agentScript = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(userSlider, 0, 0), Obj(agentScript, 0, 0)],
            [Wire(userSlider, agentScript)]);

        // Seed is only the agent's node, but the wired user slider is part of the same dataflow cluster.
        var moves = CanvasLayout.Arrange(canvas, new[] { agentScript });

        Assert.True(Pos(canvas, moves, userSlider).X < Pos(canvas, moves, agentScript).X);
    }

    [Fact]
    public void KeepsSameGroupNodesContiguousInAColumn()
    {
        // Three sliders all feed one script, so all three land in layer 0. Two of them are grouped; their
        // initial Y ordering interleaves the odd one out.
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var ungrouped = Guid.NewGuid();
        var script = Guid.NewGuid();
        var group = new GroupState(Guid.NewGuid(), "pair", new[] { g1, g2 }, 0);
        var canvas = Snapshot(
            [Obj(g1, 0, 0), Obj(ungrouped, 0, 50), Obj(g2, 0, 100), Obj(script, 0, 50)],
            [Wire(g1, script), Wire(ungrouped, script), Wire(g2, script)],
            [group]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        // Order the layer-0 nodes by their resulting Y; the two group members must be adjacent.
        var column = new[] { g1, g2, ungrouped }
            .OrderBy(id => Pos(canvas, moves, id).Y)
            .ToArray();
        var i1 = Array.IndexOf(column, g1);
        var i2 = Array.IndexOf(column, g2);
        Assert.Equal(1, Math.Abs(i1 - i2));
    }

    [Fact]
    public void StraightensAMergeNodeOntoTheMeanOfItsInputs()
    {
        // t1,t2 feed m; t3 feeds m2; m and m2 both feed out — one cluster, so t3 shares the input column but
        // is NOT wired to m. Pure column-centering would park m at the shared band center (mean of all three
        // t's); wire-straightening must instead sit m on the mean of ONLY its two real inputs.
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var m = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var output = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(t1, 0, 0), Obj(t2, 0, 0), Obj(t3, 0, 0), Obj(m, 0, 0), Obj(m2, 0, 0), Obj(output, 0, 0)],
            [Wire(t1, m), Wire(t2, m), Wire(t3, m2), Wire(m, output), Wire(m2, output)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { m });

        var meanOfInputs = (Pos(canvas, moves, t1).Y + Pos(canvas, moves, t2).Y) / 2f;
        Assert.True(Math.Abs(Pos(canvas, moves, m).Y - meanOfInputs) < 1.0f,
            "merge node must align to the mean Y of exactly its two inputs");
        // ... and therefore not simply share the band center with the unrelated m2 (fed by t3 alone).
        Assert.True(Math.Abs(Pos(canvas, moves, m).Y - Pos(canvas, moves, m2).Y) > 1.0f);
    }

    [Fact]
    public void SeparatesDifferentGroupsInAColumnByTheGroupGap()
    {
        // Two groups of two sliders all feed one script: all four share layer 0. Members of a group sit one
        // RowGap apart; the boundary between the two groups gets an extra GroupGap of clearance.
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var script = Guid.NewGuid();
        var canvas = Snapshot(
            [Obj(a1, 0, 0), Obj(a2, 0, 0), Obj(b1, 0, 0), Obj(b2, 0, 0), Obj(script, 0, 0)],
            [Wire(a1, script), Wire(a2, script), Wire(b1, script), Wire(b2, script)],
            [new GroupState(Guid.NewGuid(), "A", new[] { a1, a2 }, 0),
             new GroupState(Guid.NewGuid(), "B", new[] { b1, b2 }, 0)]);

        var moves = CanvasLayout.Arrange(canvas, new[] { script });

        var ys = new[] { a1, a2, b1, b2 }.Select(id => Pos(canvas, moves, id).Y).OrderBy(y => y).ToArray();
        var gaps = new[] { ys[1] - ys[0], ys[2] - ys[1], ys[3] - ys[2] };
        // The group boundary is the single widest gap; it exceeds the within-group gaps by exactly GroupGap.
        Assert.True(gaps.Max() - gaps.Min() > 20f, "the between-group gap must exceed the within-group gap");
        Assert.True(Math.Abs((gaps.Max() - gaps.Min()) - 26f) < 2f, "the extra clearance equals GroupGap (26)");
    }

    [Fact]
    public void EmptySeedSetProducesNoMoves()
    {
        var canvas = Snapshot([Obj(Guid.NewGuid(), 0, 0)]);
        Assert.Empty(CanvasLayout.Arrange(canvas, Array.Empty<Guid>()));
    }
}
