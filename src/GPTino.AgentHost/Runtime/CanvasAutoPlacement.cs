using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Deterministic, server-owned component placement for canvas.create operations whose model-supplied
/// pivot is the sentinel <see cref="AutoPivotSentinel"/>. The model declares topology intent (which
/// components/sliders feed the new one, via <c>autoUpstream</c> and same-ChangeSet connect wires);
/// the server owns the coordinates and computes a clean, non-overlapping downstream position
/// (source-left → result-right) just before bridge dispatch.
///
/// <para>
/// The core is a PURE function of (sentinel creates, same-ChangeSet wire edges, the pre-write canvas
/// snapshot) — see <see cref="ComputePlacements"/>: identical input always yields identical output,
/// and it NEVER moves an existing object. Human-placed geometry is an immutable collision obstacle;
/// the algorithm only ever assigns a pivot to a brand-new component (already declared gptino:absent).
/// </para>
///
/// <para>
/// Scope cut (Phase 1): there is NO post-hoc full-canvas cleanup / tidy pass. A just-created
/// component's real Bounds are unknown until Grasshopper emits it, so placement uses a single fixed
/// size estimate; a later phase can add a broker-computed batch relayout of agent-created components
/// (via the existing canvas.move batch op) once the true sizes are known.
/// </para>
/// </summary>
internal static class CanvasAutoPlacement
{
    /// <summary>The pivot value that opts a canvas.create into server-computed placement.</summary>
    internal const string AutoPivotSentinel = "gptino:auto";

    // Phase 1 tuning, in Grasshopper canvas pixels. Fixed single estimate for a not-yet-emitted
    // component; long panels/wide sliders may still slightly overlap (a known, accepted Phase-1
    // limitation, correctable by the deferred cleanup pass).
    private const float EstimatedWidth = 160f;
    private const float EstimatedHeight = 80f;
    private const float Padding = 20f;          // clearance kept around every occupied rectangle
    private const float HorizontalGap = 90f;    // gap injected downstream of the max upstream right edge
    private const float VerticalStep = 40f;     // collision-search step
    private const int MaxVerticalSteps = 400;   // bounded vertical search before bumping x by one column

    /// <summary>A canvas.create needing a computed pivot: its id, declared upstream, operation order.</summary>
    internal readonly record struct AutoCreate(Guid ObjectId, IReadOnlyList<Guid> Upstream, int Order);

    /// <summary>A same-ChangeSet connect wire (source feeds target).</summary>
    internal readonly record struct AutoWire(Guid Source, Guid Target);

    /// <summary>
    /// Rewrites every canvas.create whose pivot is the sentinel into a concrete <c>pivot:{x,y}</c>
    /// with <c>autoUpstream</c> stripped, so the unchanged Grasshopper adapter always receives today's
    /// exact contract. Non-sentinel operations pass through untouched. The frozen payload is never
    /// touched here — only the dispatched <see cref="LiveDocumentBackend.PreparedOperation.Arguments"/>
    /// change — so the idempotency hash and reserved artifacts are unaffected.
    /// </summary>
    internal static IReadOnlyList<LiveDocumentBackend.PreparedOperation> ResolveAutoPivots(
        IReadOnlyList<LiveDocumentBackend.PreparedOperation> operations,
        CanvasSnapshot canvas)
    {
        var creates = new List<AutoCreate>();
        for (var index = 0; index < operations.Count; index++)
        {
            if (IsSentinelCreate(operations[index], out var objectId))
            {
                creates.Add(new AutoCreate(objectId, ReadAutoUpstream(operations[index].Arguments), index));
            }
        }

        // Fast path: no sentinel creates means nothing to compute — return the same list unchanged so
        // every ordinary execution pays only one scan.
        if (creates.Count == 0)
        {
            return operations;
        }

        var wireEdges = new List<AutoWire>();
        foreach (var operation in operations)
        {
            if (TryReadConnectEdge(operation, out var edge))
            {
                wireEdges.Add(edge);
            }
        }

        var placements = ComputePlacements(creates, wireEdges, canvas.Objects);

        var rewritten = new List<LiveDocumentBackend.PreparedOperation>(operations.Count);
        foreach (var operation in operations)
        {
            if (IsSentinelCreate(operation, out var objectId) &&
                placements.TryGetValue(objectId, out var pivot))
            {
                rewritten.Add(operation with { Arguments = RewriteArguments(operation.Arguments, pivot) });
            }
            else
            {
                rewritten.Add(operation);
            }
        }
        return rewritten;
    }

    /// <summary>
    /// PURE placement core. Given the sentinel creates, the same-ChangeSet connect wires, and the
    /// pre-write canvas objects, returns a concrete pivot for every create. Fully deterministic:
    /// identical input yields identical output, and no existing object is ever moved.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, CanvasPoint> ComputePlacements(
        IReadOnlyList<AutoCreate> creates,
        IReadOnlyList<AutoWire> wireEdges,
        IReadOnlyList<CanvasObjectState> existing)
    {
        var createdIds = new HashSet<Guid>(creates.Select(create => create.ObjectId));

        // Upstream edges = union of each create's declared autoUpstream and every same-ChangeSet connect
        // wire whose target is one of these creates (upstream = the wire source). Sources may be existing
        // objects or sibling creates. SortedSet keeps aggregation order-independent for determinism.
        var upstream = new Dictionary<Guid, SortedSet<Guid>>();
        foreach (var create in creates)
        {
            var sources = new SortedSet<Guid>();
            foreach (var source in create.Upstream)
            {
                if (source != Guid.Empty && source != create.ObjectId)
                {
                    sources.Add(source);
                }
            }
            upstream[create.ObjectId] = sources;
        }
        foreach (var edge in wireEdges)
        {
            if (edge.Source != Guid.Empty && edge.Target != Guid.Empty && edge.Source != edge.Target &&
                upstream.TryGetValue(edge.Target, out var sources))
            {
                sources.Add(edge.Source);
            }
        }

        var order = creates.ToDictionary(create => create.ObjectId, create => create.Order);
        var ordered = TopologicalOrder(creates, upstream, createdIds, order);

        // Occupancy grid: every existing object is an immutable obstacle; each placed create joins it.
        var occupancy = new List<PlacementRect>();
        var rects = new Dictionary<Guid, PlacementRect>();
        var pivots = new Dictionary<Guid, CanvasPoint>();
        foreach (var item in existing)
        {
            var rect = ExistingRect(item);
            occupancy.Add(rect);
            rects[item.ObjectId] = rect;
            pivots[item.ObjectId] = item.Pivot;
        }
        var existingBounds = BoundingBox(occupancy);

        var result = new Dictionary<Guid, CanvasPoint>();
        foreach (var objectId in ordered)
        {
            // Only upstream that are already placed (existing objects, or creates earlier in topo order)
            // can anchor a column; a not-yet-placed cyclic upstream is skipped.
            var placedUpstream = upstream[objectId]
                .Where(rects.ContainsKey)
                .ToList();

            float anchorX;
            float anchorY;
            if (placedUpstream.Count > 0)
            {
                // Downstream column: to the right of the furthest upstream edge, vertically centered on
                // the mean upstream pivot Y (source-left → result-right by construction).
                var maxRightEdge = placedUpstream.Max(source => rects[source].RightEdge);
                anchorX = maxRightEdge + HorizontalGap + (EstimatedWidth / 2f);
                anchorY = (float)placedUpstream.Average(source => pivots[source].Y);
            }
            else if (existingBounds is { } bounds)
            {
                // No resolvable upstream: open a fresh column below the existing content bbox at its
                // left edge. A create that itself feeds another create still lands left of that consumer,
                // because the consumer's X is derived from THIS create's right edge once it is placed.
                anchorX = bounds.X + (EstimatedWidth / 2f);
                anchorY = bounds.BottomEdge + Padding + (EstimatedHeight / 2f);
            }
            else
            {
                // Empty canvas: anchor at the origin.
                anchorX = 0f;
                anchorY = 0f;
            }

            var pivot = ResolveFreePivot(anchorX, anchorY, occupancy);
            result[objectId] = pivot;
            var placed = CenteredRect(pivot, EstimatedWidth, EstimatedHeight);
            occupancy.Add(placed);
            rects[objectId] = placed;
            pivots[objectId] = pivot;
        }
        return result;
    }

    // Kahn topological sort over create→create edges only (existing-object upstream are already placed,
    // so they never constrain create ordering). Stable tie-break = operation order; a cycle falls back to
    // operation order for the unresolved remainder (a real wire cycle is rejected later by RejectCycles).
    private static IReadOnlyList<Guid> TopologicalOrder(
        IReadOnlyList<AutoCreate> creates,
        IReadOnlyDictionary<Guid, SortedSet<Guid>> upstream,
        HashSet<Guid> createdIds,
        IReadOnlyDictionary<Guid, int> order)
    {
        var inDegree = new Dictionary<Guid, int>();
        var consumers = new Dictionary<Guid, List<Guid>>();
        foreach (var create in creates)
        {
            inDegree[create.ObjectId] = 0;
        }
        foreach (var create in creates)
        {
            foreach (var source in upstream[create.ObjectId])
            {
                if (!createdIds.Contains(source))
                {
                    continue;
                }
                inDegree[create.ObjectId]++;
                if (!consumers.TryGetValue(source, out var list))
                {
                    list = new List<Guid>();
                    consumers[source] = list;
                }
                list.Add(create.ObjectId);
            }
        }

        var ready = new SortedSet<(int Order, Guid Id)>();
        foreach (var create in creates)
        {
            if (inDegree[create.ObjectId] == 0)
            {
                ready.Add((order[create.ObjectId], create.ObjectId));
            }
        }

        var result = new List<Guid>(creates.Count);
        var emitted = new HashSet<Guid>();
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            result.Add(next.Id);
            emitted.Add(next.Id);
            if (!consumers.TryGetValue(next.Id, out var list))
            {
                continue;
            }
            foreach (var consumer in list.OrderBy(id => order[id]))
            {
                if (--inDegree[consumer] == 0)
                {
                    ready.Add((order[consumer], consumer));
                }
            }
        }

        if (result.Count < creates.Count)
        {
            foreach (var create in creates.Where(c => !emitted.Contains(c.ObjectId)).OrderBy(c => c.Order))
            {
                result.Add(create.ObjectId);
            }
        }
        return result;
    }

    // Walks the anchor column top-to-bottom in alternating +/- steps; when a column is exhausted it
    // bumps x by one HorizontalGap and retries. Deterministic and always terminating (free space always
    // exists to the right).
    private static CanvasPoint ResolveFreePivot(
        float anchorX,
        float anchorY,
        IReadOnlyList<PlacementRect> occupancy)
    {
        for (var column = 0; column < 1024; column++)
        {
            var x = anchorX + (column * HorizontalGap);
            for (var step = 0; step <= MaxVerticalSteps; step++)
            {
                if (step == 0)
                {
                    if (TryColumn(x, anchorY, occupancy, out var centered))
                    {
                        return centered;
                    }
                    continue;
                }
                if (TryColumn(x, anchorY - (step * VerticalStep), occupancy, out var above))
                {
                    return above;
                }
                if (TryColumn(x, anchorY + (step * VerticalStep), occupancy, out var below))
                {
                    return below;
                }
            }
        }
        // Unreachable in practice; a deterministic last resort keeps the method total.
        return new CanvasPoint(anchorX, anchorY);
    }

    private static bool TryColumn(
        float x,
        float y,
        IReadOnlyList<PlacementRect> occupancy,
        out CanvasPoint pivot)
    {
        var candidate = CenteredRect(new CanvasPoint(x, y), EstimatedWidth, EstimatedHeight);
        if (IsFree(candidate, occupancy))
        {
            pivot = new CanvasPoint(x, y);
            return true;
        }
        pivot = default;
        return false;
    }

    // A candidate is free when its rectangle, grown by Padding on every side, touches no occupied
    // rectangle. Only the candidate is padded, so the guaranteed clearance from any existing object is
    // exactly Padding — which also means the unpadded new box never overlaps an existing box.
    private static bool IsFree(PlacementRect candidate, IReadOnlyList<PlacementRect> occupancy)
    {
        var padded = candidate.Inflate(Padding);
        foreach (var rect in occupancy)
        {
            if (padded.Intersects(rect))
            {
                return false;
            }
        }
        return true;
    }

    private static PlacementRect ExistingRect(CanvasObjectState item)
    {
        var width = item.Bounds.Width;
        var height = item.Bounds.Height;
        if (item.BoundsOrigin is { } origin)
        {
            return new PlacementRect(origin.X, origin.Y, width, height);
        }
        // Pivot-centered fallback when the adapter did not report BoundsOrigin (older bridge / test fakes).
        return new PlacementRect(item.Pivot.X - (width / 2f), item.Pivot.Y - (height / 2f), width, height);
    }

    private static PlacementRect CenteredRect(CanvasPoint center, float width, float height) =>
        new(center.X - (width / 2f), center.Y - (height / 2f), width, height);

    private static PlacementRect? BoundingBox(IReadOnlyList<PlacementRect> rects)
    {
        if (rects.Count == 0)
        {
            return null;
        }
        var minX = rects.Min(rect => rect.X);
        var minY = rects.Min(rect => rect.Y);
        var maxX = rects.Max(rect => rect.RightEdge);
        var maxY = rects.Max(rect => rect.BottomEdge);
        return new PlacementRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool IsSentinelCreate(LiveDocumentBackend.PreparedOperation operation, out Guid objectId)
    {
        objectId = Guid.Empty;
        if (!string.Equals(operation.BridgeOperation, "canvas.create", StringComparison.Ordinal))
        {
            return false;
        }
        if (!operation.Arguments.TryGetProperty("pivot", out var pivot) ||
            pivot.ValueKind != JsonValueKind.String ||
            !string.Equals(pivot.GetString(), AutoPivotSentinel, StringComparison.Ordinal))
        {
            return false;
        }
        return operation.Arguments.TryGetProperty("objectId", out var idElement) &&
            idElement.TryGetGuid(out objectId);
    }

    private static IReadOnlyList<Guid> ReadAutoUpstream(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("autoUpstream", out var upstream) ||
            upstream.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Guid>();
        }
        var ids = new List<Guid>();
        foreach (var element in upstream.EnumerateArray())
        {
            if (element.TryGetGuid(out var id) && id != Guid.Empty)
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static bool TryReadConnectEdge(LiveDocumentBackend.PreparedOperation operation, out AutoWire edge)
    {
        edge = default;
        if (!string.Equals(operation.BridgeOperation, "canvas.setWire", StringComparison.Ordinal))
        {
            return false;
        }
        if (!operation.Arguments.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String ||
            !string.Equals(action.GetString(), "connect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!operation.Arguments.TryGetProperty("wire", out var wire) ||
            wire.ValueKind != JsonValueKind.Object ||
            !wire.TryGetProperty("sourceObjectId", out var source) || !source.TryGetGuid(out var sourceId) ||
            !wire.TryGetProperty("targetObjectId", out var target) || !target.TryGetGuid(out var targetId))
        {
            return false;
        }
        edge = new AutoWire(sourceId, targetId);
        return true;
    }

    // Produces the concrete arguments the bridge expects: pivot becomes {x,y}, autoUpstream is removed,
    // every other property (operationId/objectId/componentTypeId/nickName) is preserved verbatim. The
    // result therefore matches CreateCanvasObjectRequest exactly under BridgeProtocol.JsonOptions.
    private static JsonElement RewriteArguments(JsonElement arguments, CanvasPoint pivot)
    {
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node.Remove("autoUpstream");
        node["pivot"] = new JsonObject
        {
            ["x"] = pivot.X,
            ["y"] = pivot.Y,
        };
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private readonly record struct PlacementRect(float X, float Y, float Width, float Height)
    {
        public float RightEdge => X + Width;

        public float BottomEdge => Y + Height;

        public PlacementRect Inflate(float pad) =>
            new(X - pad, Y - pad, Width + (2f * pad), Height + (2f * pad));

        // Half-open overlap: rectangles that only share an edge do not intersect.
        public bool Intersects(PlacementRect other) =>
            X < other.RightEdge && RightEdge > other.X &&
            Y < other.BottomEdge && BottomEdge > other.Y;
    }
}
