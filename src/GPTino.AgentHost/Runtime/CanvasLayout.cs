using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Deterministic, server-owned "tidy" layout: given the live canvas and a set of seed components the
/// agent just authored, arrange the whole dataflow cluster those seeds belong to like a human would —
/// inputs on the left, script stages flowing left→right, outputs on the right, stacked top→bottom, with
/// same-group nodes kept contiguous. A layered (Sugiyama-style) graph layout computed purely from wire
/// TOPOLOGY and real component BOUNDS — it never reads a component's source or behaviour, so it costs no
/// model inference and no extra context.
///
/// <para>
/// PURE and deterministic: <see cref="Arrange"/> is a function of (snapshot, seeds) — identical input
/// yields identical output, so re-running it on an already-tidy graph is a no-op (idempotent). Scope is
/// the weakly-connected wire cluster(s) containing a seed; a component in no such cluster is never moved,
/// so a user's separate hand-built definition is left alone.
/// </para>
/// </summary>
internal static class CanvasLayout
{
    internal sealed record Options(
        float ColumnGap = 110f,   // horizontal clearance between one layer's right edge and the next
        float RowGap = 28f,       // vertical clearance between stacked nodes in a column
        float GroupGap = 26f,     // extra vertical clearance between different groups in the same column
        float MoveEpsilon = 1.5f, // pivots that move less than this are left untouched (avoids churn)
        int BarycenterSweeps = 4) // crossing-reduction passes (down/up alternating)
    {
        internal static readonly Options Default = new();
    }

    /// <summary>
    /// Returns the new pivot for every component that should move to tidy the cluster(s) the
    /// <paramref name="seedIds"/> belong to. Components already at (or within <see cref="Options.MoveEpsilon"/>
    /// of) their computed spot are omitted, so the result is exactly the set of real moves.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, CanvasPoint> Arrange(
        CanvasSnapshot canvas,
        IReadOnlyCollection<Guid> seedIds,
        Options? options = null)
    {
        options ??= Options.Default;
        var byId = canvas.Objects
            .GroupBy(o => o.ObjectId)
            .ToDictionary(g => g.Key, g => g.First());

        var (outgoing, incoming, undirected) = BuildEdges(canvas, byId);
        var scope = ExpandToClusters(seedIds, byId, undirected);
        if (scope.Count == 0)
        {
            return new Dictionary<Guid, CanvasPoint>();
        }

        var layer = AssignLayers(scope, incoming, outgoing);
        var columns = OrderWithinLayers(scope, layer, incoming, outgoing, byId, canvas.Groups, options);
        var targets = AssignCoordinates(columns, byId, options);

        // Emit only the components whose pivot actually changes — keeps the resulting canvas.move minimal
        // and makes a re-run on an already-tidy cluster a genuine no-op.
        var moves = new Dictionary<Guid, CanvasPoint>();
        foreach (var (id, pivot) in targets)
        {
            var current = byId[id].Pivot;
            if (Math.Abs(current.X - pivot.X) > options.MoveEpsilon ||
                Math.Abs(current.Y - pivot.Y) > options.MoveEpsilon)
            {
                moves[id] = pivot;
            }
        }
        return moves;
    }

    // ---- topology ----

    private static (Dictionary<Guid, SortedSet<Guid>> Outgoing,
                    Dictionary<Guid, SortedSet<Guid>> Incoming,
                    Dictionary<Guid, SortedSet<Guid>> Undirected)
        BuildEdges(CanvasSnapshot canvas, IReadOnlyDictionary<Guid, CanvasObjectState> byId)
    {
        var outgoing = new Dictionary<Guid, SortedSet<Guid>>();
        var incoming = new Dictionary<Guid, SortedSet<Guid>>();
        var undirected = new Dictionary<Guid, SortedSet<Guid>>();

        void AddEdge(Guid source, Guid target)
        {
            if (source == target || !byId.ContainsKey(source) || !byId.ContainsKey(target))
            {
                return;
            }
            (outgoing.TryGetValue(source, out var o) ? o : outgoing[source] = new SortedSet<Guid>()).Add(target);
            (incoming.TryGetValue(target, out var i) ? i : incoming[target] = new SortedSet<Guid>()).Add(source);
            (undirected.TryGetValue(source, out var us) ? us : undirected[source] = new SortedSet<Guid>()).Add(target);
            (undirected.TryGetValue(target, out var ut) ? ut : undirected[target] = new SortedSet<Guid>()).Add(source);
        }

        // Wires are the authoritative topology; also union each input's CurrentSources so a snapshot that
        // reports connectivity only on the parameter still yields the full graph.
        foreach (var wire in canvas.Wires)
        {
            AddEdge(wire.SourceObjectId, wire.TargetObjectId);
        }
        foreach (var obj in canvas.Objects)
        {
            foreach (var input in obj.Inputs)
            {
                foreach (var endpoint in input.CurrentSources)
                {
                    AddEdge(endpoint.OwnerObjectId, obj.ObjectId);
                }
            }
        }
        return (outgoing, incoming, undirected);
    }

    private static IReadOnlySet<Guid> ExpandToClusters(
        IReadOnlyCollection<Guid> seedIds,
        IReadOnlyDictionary<Guid, CanvasObjectState> byId,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> undirected)
    {
        var scope = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var seed in seedIds.Where(byId.ContainsKey).OrderBy(id => id))
        {
            if (scope.Add(seed))
            {
                queue.Enqueue(seed);
            }
        }
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!undirected.TryGetValue(node, out var neighbours))
            {
                continue;
            }
            foreach (var neighbour in neighbours)
            {
                if (scope.Add(neighbour))
                {
                    queue.Enqueue(neighbour);
                }
            }
        }
        return scope;
    }

    // ---- layering (longest path from sources; back-edges of any cycle are ignored) ----

    private static IReadOnlyDictionary<Guid, int> AssignLayers(
        IReadOnlySet<Guid> scope,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> incoming,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> outgoing)
    {
        Guid[] InScope(Guid node, IReadOnlyDictionary<Guid, SortedSet<Guid>> map) =>
            map.TryGetValue(node, out var set) ? set.Where(scope.Contains).ToArray() : Array.Empty<Guid>();

        // Kahn traversal so each node is layered after all its in-scope predecessors. In-degree counts only
        // in-scope edges. A residual cycle (rare in GH — feedback loops) is broken by draining the lowest-id
        // remaining node at its current layer, which forgives the back-edge without failing.
        var inDegree = scope.ToDictionary(id => id, id => InScope(id, incoming).Length);
        var layer = scope.ToDictionary(id => id, _ => 0);
        var ready = new SortedSet<Guid>(scope.Where(id => inDegree[id] == 0));
        var settled = new HashSet<Guid>();

        while (settled.Count < scope.Count)
        {
            if (ready.Count == 0)
            {
                // Cycle fallback: release the lowest-id unsettled node.
                var stuck = scope.Where(id => !settled.Contains(id)).Min();
                ready.Add(stuck);
            }
            var node = ready.Min;
            ready.Remove(node);
            if (!settled.Add(node))
            {
                continue;
            }
            foreach (var target in InScope(node, outgoing))
            {
                if (layer[target] < layer[node] + 1)
                {
                    layer[target] = layer[node] + 1;
                }
                if (!settled.Contains(target) && --inDegree[target] <= 0)
                {
                    ready.Add(target);
                }
            }
        }
        return layer;
    }

    // ---- within-layer ordering: barycenter crossing reduction + group contiguity ----

    private static IReadOnlyList<IReadOnlyList<Guid>> OrderWithinLayers(
        IReadOnlySet<Guid> scope,
        IReadOnlyDictionary<Guid, int> layer,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> incoming,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> outgoing,
        IReadOnlyDictionary<Guid, CanvasObjectState> byId,
        IReadOnlyList<GroupState> groups,
        Options options)
    {
        var maxLayer = layer.Values.Max();
        var layers = new List<List<Guid>>();
        for (var l = 0; l <= maxLayer; l++)
        {
            // Seed each layer's order by the components' current Y so the tidy roughly preserves the
            // user's vertical intent; id breaks ties for determinism.
            layers.Add(scope.Where(id => layer[id] == l)
                .OrderBy(id => byId[id].Pivot.Y)
                .ThenBy(id => id)
                .ToList());
        }

        var position = layers.SelectMany(col => col.Select((id, index) => (id, index)))
            .ToDictionary(p => p.id, p => (double)p.index);

        double Barycenter(Guid node, IReadOnlyDictionary<Guid, SortedSet<Guid>> map, double fallback)
        {
            var neighbours = map.TryGetValue(node, out var set)
                ? set.Where(scope.Contains).ToArray()
                : Array.Empty<Guid>();
            return neighbours.Length == 0 ? fallback : neighbours.Average(n => position[n]);
        }

        for (var sweep = 0; sweep < options.BarycenterSweeps; sweep++)
        {
            var downward = sweep % 2 == 0;
            var indices = downward
                ? Enumerable.Range(1, layers.Count - 1)
                : Enumerable.Range(0, layers.Count - 1).Reverse();
            foreach (var l in indices)
            {
                var relative = downward ? incoming : outgoing;
                layers[l] = layers[l]
                    .Select(id => (id, key: Barycenter(id, relative, position[id])))
                    .OrderBy(p => p.key)
                    .ThenBy(p => p.id)
                    .Select(p => p.id)
                    .ToList();
                for (var index = 0; index < layers[l].Count; index++)
                {
                    position[layers[l][index]] = index;
                }
            }
        }

        // Group contiguity: within each layer, pull members of the same group together. Groups are ordered
        // by their members' mean barycenter so a group lands where its wires already want it.
        var groupOf = new Dictionary<Guid, Guid>();
        foreach (var group in groups)
        {
            foreach (var member in group.ObjectIds)
            {
                groupOf[member] = group.GroupId; // last wins; a node in multiple groups picks the last listed
            }
        }
        Guid GroupKey(Guid id) => groupOf.TryGetValue(id, out var g) ? g : id;
        for (var l = 0; l < layers.Count; l++)
        {
            var column = layers[l];
            var groupRank = column
                .Select((id, index) => (key: GroupKey(id), index))
                .GroupBy(p => p.key)
                .ToDictionary(g => g.Key, g => g.Average(p => (double)p.index));
            // Sort by (group mean position, group key, within-group order): the group-key tie-break keeps a
            // group's members strictly contiguous even when its mean collides with a neighbour's position.
            layers[l] = column
                .Select((id, index) => (id, index, key: GroupKey(id)))
                .OrderBy(p => groupRank[p.key])
                .ThenBy(p => p.key)
                .ThenBy(p => p.index)
                .Select(p => p.id)
                .ToList();
        }

        return layers;
    }

    // ---- coordinate assignment from real bounds ----

    private static IReadOnlyDictionary<Guid, CanvasPoint> AssignCoordinates(
        IReadOnlyList<IReadOnlyList<Guid>> layers,
        IReadOnlyDictionary<Guid, CanvasObjectState> byId,
        Options options)
    {
        // Column widths / heights from REAL bounds so nothing overlaps and spacing is even.
        var columnWidth = layers
            .Select(col => col.Count == 0 ? 0f : col.Max(id => byId[id].Bounds.Width))
            .ToArray();
        var columnHeight = layers
            .Select(col => col.Sum(id => byId[id].Bounds.Height)
                + Math.Max(0, col.Count - 1) * options.RowGap)
            .ToArray();
        var tallest = columnHeight.DefaultIfEmpty(0f).Max();

        // Anchor the tidied cluster at its current top-left so it stays roughly where the user had it.
        var anchorX = layers.SelectMany(c => c).Select(id => TopLeft(byId[id]).X).DefaultIfEmpty(0f).Min();
        var anchorY = layers.SelectMany(c => c).Select(id => TopLeft(byId[id]).Y).DefaultIfEmpty(0f).Min();

        var result = new Dictionary<Guid, CanvasPoint>();
        var columnLeft = anchorX;
        for (var l = 0; l < layers.Count; l++)
        {
            var column = layers[l];
            var centerX = columnLeft + columnWidth[l] / 2f;
            // Vertically center each column against the tallest so layers read as one balanced band.
            var y = anchorY + (tallest - columnHeight[l]) / 2f;
            foreach (var id in column)
            {
                var obj = byId[id];
                var top = y;
                var boundsCenter = new CanvasPoint(centerX, top + obj.Bounds.Height / 2f);
                result[id] = PivotForBoundsCenter(obj, boundsCenter);
                y += obj.Bounds.Height + options.RowGap;
            }
            columnLeft += columnWidth[l] + options.ColumnGap;
        }
        return result;
    }

    private static CanvasPoint TopLeft(CanvasObjectState obj) =>
        obj.BoundsOrigin ?? new CanvasPoint(
            obj.Pivot.X - obj.Bounds.Width / 2f,
            obj.Pivot.Y - obj.Bounds.Height / 2f);

    // The adapter positions by Pivot, but some objects (panels) anchor their pivot away from the bounding-box
    // center. Preserve each object's pivot→bounds offset so the computed bounding box lands exactly where we
    // want, whatever the pivot convention.
    private static CanvasPoint PivotForBoundsCenter(CanvasObjectState obj, CanvasPoint boundsCenter)
    {
        if (obj.BoundsOrigin is not { } origin)
        {
            return boundsCenter; // pivot == center fallback
        }
        var currentCenter = new CanvasPoint(
            origin.X + obj.Bounds.Width / 2f,
            origin.Y + obj.Bounds.Height / 2f);
        return new CanvasPoint(
            obj.Pivot.X + (boundsCenter.X - currentCenter.X),
            obj.Pivot.Y + (boundsCenter.Y - currentCenter.Y));
    }
}
