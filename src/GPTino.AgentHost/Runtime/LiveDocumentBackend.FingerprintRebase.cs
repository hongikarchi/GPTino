using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CanvasSceneAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.ScriptAdapter;

namespace GPTino.AgentHost.Runtime;

// gptino:auto expectation resolution and self-stale concrete-fingerprint rebase against the session resource ledger.
public sealed partial class LiveDocumentBackend
{
    // Resolves gptino:auto read/write expectations against the live snapshot, gated by the per-session
    // resource ledger: an auto expectation is filled with the live fingerprint ONLY when THIS session wrote
    // the resource and it has not changed since (self-sequential). A foreign-session write, a manual
    // Grasshopper edit, an absent resource, or a resource this session never wrote is REFUSED and returned as
    // a conflict so the existing Blocked path stops it. Runs on the single broker worker thread, so the
    // ledger read cannot race a commit.
    internal static (ChangeSet Resolved, IReadOnlyList<string> Conflicts) ResolveAutoExpectations(
        ChangeSet changeSet,
        StateSnapshot liveState,
        Guid sessionId,
        IReadOnlyDictionary<string, ResourceLedgerEntry> resourceLedger)
    {
        if (!changeSet.ReadSet.Concat(changeSet.WriteSet).Any(expectation => expectation.IsAuto))
        {
            return (changeSet, Array.Empty<string>());
        }

        var conflicts = new List<string>();

        ResourceExpectation Resolve(ResourceExpectation expectation)
        {
            if (!expectation.IsAuto)
            {
                return expectation;
            }
            var key = $"{expectation.Resource.Kind}:{expectation.Resource.Id}:{expectation.Resource.Field}";
            var live = liveState.Resources.FirstOrDefault(item =>
                ExactDomainOverlaps(item.Resource, expectation.Resource));
            if (live is null || string.IsNullOrWhiteSpace(live.Fingerprint))
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: the resource is absent from the live document. " +
                    "Create it first, or supply a concrete fingerprint.");
                return expectation;
            }
            if (!resourceLedger.TryGetValue(key, out var ledger))
            {
                // Fallback: a Python/Rhino sub-domain may lack its own ledger row (e.g. the first setComponentIo
                // right after createComponent), yet the parent component/object this session created still has a
                // ledger row. If this session owns the parent AND the parent's own fingerprint is unchanged
                // (no foreign session write and no manual edit touched the component or any sub-domain since),
                // resolve the sub-domain auto to its own live fingerprint. A foreign change moves the parent
                // fingerprint, so this still declines.
                var parent = ParentResource(expectation.Resource);
                if (parent is not null)
                {
                    var parentLive = liveState.Resources.FirstOrDefault(item =>
                        ExactDomainOverlaps(item.Resource, parent));
                    var parentEntry = resourceLedger.Values.FirstOrDefault(entry =>
                        entry.SessionId == sessionId && ExactDomainOverlaps(entry.Resource, parent));
                    if (parentLive is not null &&
                        parentEntry.Resource is not null &&
                        string.Equals(parentEntry.Fingerprint, parentLive.Fingerprint, StringComparison.Ordinal))
                    {
                        return expectation with { ExpectedFingerprint = live.Fingerprint };
                    }
                }
                conflicts.Add(
                    $"gptino:auto declined for {key}: this session has not written it, so there is no " +
                    $"baseline to fill (editing a pre-existing component). Current fingerprint: {live.Fingerprint}. " +
                    "Resubmit that resource with this concrete value directly.");
                return expectation;
            }
            if (ledger.SessionId != sessionId)
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: another session wrote it after this session last did. " +
                    $"Current fingerprint: {live.Fingerprint}. Re-read and resubmit with this value.");
                return expectation;
            }
            if (!string.Equals(ledger.Fingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: it drifted (a manual Grasshopper edit) since this session " +
                    $"last wrote it. Current fingerprint: {live.Fingerprint}. Re-read and resubmit with this value.");
                return expectation;
            }
            return expectation with { ExpectedFingerprint = live.Fingerprint };
        }

        var readSet = changeSet.ReadSet.Select(Resolve).ToArray();
        var writeSet = changeSet.WriteSet.Select(Resolve).ToArray();
        if (conflicts.Count > 0)
        {
            return (changeSet, conflicts);
        }
        return (changeSet with { ReadSet = readSet, WriteSet = writeSet }, Array.Empty<string>());
    }

    /// <summary>
    /// Auto-rebases SELF-ATTRIBUTABLE stale concrete fingerprints to live. Value/geometry writes
    /// (setNumberSlider, move, delete, rhino transform/upsert) carry a concrete fingerprint that
    /// gptino:auto cannot fill, so a session that already advanced a resource's fingerprint with its
    /// OWN prior commit then submits a stale base and Blocks — the dominant conflict in the field.
    /// Using the exact same safety test as <see cref="ResolveAutoExpectations"/> (the current live
    /// fingerprint equals what THIS session last wrote, per the ledger — no foreign write, no manual
    /// drift), we rebase both the writeSet expectation AND the operation payload fingerprint to live.
    /// A foreign/drifted resource is left untouched, so <see cref="ConflictDetector"/> still Blocks a
    /// genuine conflict. Returns the (possibly) rewritten change set and operations plus the rebased
    /// resource keys for logging.
    /// </summary>
    internal static (ChangeSet ChangeSet, IReadOnlyList<PreparedOperation> Operations, IReadOnlyList<(ResourceAddress Resource, string StaleFingerprint, string LiveFingerprint)> Rebased)
        ResolveSelfStaleConcreteRebase(
            ChangeSet changeSet,
            IReadOnlyList<PreparedOperation> operations,
            StateSnapshot liveState,
            Guid sessionId,
            IReadOnlyDictionary<string, ResourceLedgerEntry> resourceLedger)
    {
        var rebased = new List<(ResourceAddress Resource, string StaleFingerprint, string LiveFingerprint)>();
        var staleToLive = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expectation in changeSet.WriteSet)
        {
            if (expectation.IsAuto || expectation.ExpectsAbsence)
            {
                continue;
            }
            var live = liveState.Resources.FirstOrDefault(resource =>
                ExactDomainOverlaps(resource.Resource, expectation.Resource));
            if (live is null ||
                string.IsNullOrWhiteSpace(live.Fingerprint) ||
                string.Equals(expectation.ExpectedFingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                continue; // absent, unmanaged, or not stale — nothing to rebase here.
            }
            var key = $"{expectation.Resource.Kind}:{expectation.Resource.Id}:{expectation.Resource.Field}";
            // Rebase ONLY when the live state is this session's own last write (no foreign write, no
            // manual drift) — identical to the gptino:auto self-sequential test.
            if (!resourceLedger.TryGetValue(key, out var ledger) ||
                ledger.SessionId != sessionId ||
                !string.Equals(ledger.Fingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                continue;
            }
            rebased.Add((expectation.Resource, expectation.ExpectedFingerprint, live.Fingerprint));
            staleToLive[expectation.ExpectedFingerprint] = live.Fingerprint;
        }
        if (rebased.Count == 0)
        {
            return (changeSet, operations, Array.Empty<(ResourceAddress, string, string)>());
        }
        var rebasedResources = rebased.Select(item => item.Resource).ToArray();
        var newWriteSet = changeSet.WriteSet
            .Select(expectation => rebasedResources.Any(resource => ExactDomainOverlaps(resource, expectation.Resource)) &&
                staleToLive.TryGetValue(expectation.ExpectedFingerprint, out var live)
                    ? expectation with { ExpectedFingerprint = live }
                    : expectation)
            .ToArray();
        var newOperations = operations
            .Select(operation =>
            {
                if (!operation.Operation.Writes.Any(write =>
                        rebasedResources.Any(resource => ExactDomainOverlaps(write, resource))))
                {
                    return operation;
                }
                var rewritten = RewritePayloadFingerprints(operation.Arguments, staleToLive);
                return rewritten is { } arguments ? operation with { Arguments = arguments } : operation;
            })
            .ToArray();
        return (changeSet with { WriteSet = newWriteSet }, newOperations, rebased);
    }

    /// <summary>
    /// Rewrites the concrete fingerprints a value/geometry payload carries: the scalar
    /// <c>expectedFingerprint</c> and any values in the <c>expectedFingerprints</c> map (canvas.move)
    /// whose value is a rebased stale fingerprint are replaced with the live one. Only
    /// <see cref="PreparedOperation.Arguments"/> is rewritten — the frozen idempotency payload is
    /// never touched. Returns null when nothing changed.
    /// </summary>
    private static JsonElement? RewritePayloadFingerprints(
        JsonElement arguments,
        IReadOnlyDictionary<string, string> staleToLive)
    {
        if (JsonNode.Parse(arguments.GetRawText()) is not JsonObject node)
        {
            return null;
        }
        var changed = false;
        if (node["expectedFingerprint"] is JsonValue scalar &&
            scalar.TryGetValue<string>(out var scalarValue) &&
            staleToLive.TryGetValue(scalarValue, out var scalarLive))
        {
            node["expectedFingerprint"] = scalarLive;
            changed = true;
        }
        if (node["expectedFingerprints"] is JsonObject map)
        {
            foreach (var entryKey in map.Select(pair => pair.Key).ToArray())
            {
                if (map[entryKey] is JsonValue value &&
                    value.TryGetValue<string>(out var mapValue) &&
                    staleToLive.TryGetValue(mapValue, out var mapLive))
                {
                    map[entryKey] = mapLive;
                    changed = true;
                }
            }
        }
        if (!changed)
        {
            return null;
        }
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    // The parent component/object of a Python/Rhino sub-domain, or null when the resource is already a
    // top-level domain. A freshly created component has no source/io/value snapshot rows yet, but its parent
    // exists; the parent's own fingerprint moves if anyone (foreign session or manual edit) touches the
    // component or its sub-domains, so the parent's unchanged fingerprint is a sound self-ownership proof.
    private static ResourceAddress? ParentResource(ResourceAddress resource) => resource.Kind switch
    {
        ResourceKind.GrasshopperComponentSource or ResourceKind.GrasshopperComponentIo or
        ResourceKind.GrasshopperComponentValue or ResourceKind.GrasshopperComponentLayout =>
            new ResourceAddress(ResourceKind.GrasshopperComponent, resource.Id, "*"),
        ResourceKind.RhinoObjectGeometry or ResourceKind.RhinoObjectAttributes =>
            new ResourceAddress(ResourceKind.RhinoObject, resource.Id, "*"),
        _ => null,
    };

    private static string? FindExpectedFingerprint(ChangeSet changeSet, TypedOperation operation)
    {
        foreach (var address in operation.Writes.Concat(operation.Reads))
        {
            var expectation = changeSet.WriteSet.Concat(changeSet.ReadSet)
                .FirstOrDefault(candidate => ExactDomainOverlaps(candidate.Resource, address));
            if (expectation is not null && !expectation.ExpectsAbsence)
            {
                return expectation.ExpectedFingerprint;
            }
        }
        return null;
    }

}
