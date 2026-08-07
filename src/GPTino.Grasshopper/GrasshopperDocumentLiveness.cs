using GPTino.BridgeContract;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Mid-operation guard against the re-entrant document teardown crash: a solve pumps the message
/// queue, a user's File&gt;Open/Close inside that pump can make Grasshopper close/replace the very
/// document the operation is mutating, and continuing to touch its native-backed state kills Rhino
/// with no crash dump. The <see cref="BridgeUiOperationScope"/> gate keeps GPTino's own
/// bookkeeping out of that window; this guard covers what the gate cannot — Grasshopper itself
/// retiring the document — by aborting the operation with a typed failure instead.
/// </summary>
internal static class GrasshopperDocumentLiveness
{
    internal const string DocumentDetachedCode = "document_detached";

    /// <summary>
    /// Call immediately after any pump-capable call (NewSolution) and before touching
    /// <paramref name="document"/> again. Throws a typed bridge failure when Grasshopper no
    /// longer hosts the document, so the job reports an honest interrupted outcome (writes so
    /// far landed in a document that went away) instead of Rhino dying.
    /// </summary>
    internal static void ThrowIfDetached(GH_Document document, string operation)
    {
        if (GrasshopperDocumentCatalog.IsDocumentHostedByGrasshopper(document))
        {
            return;
        }
        throw new BridgeProtocolException(
            DocumentDetachedCode,
            $"The Grasshopper document was closed or replaced while '{operation}' was executing; " +
            "the operation stopped immediately and later operations in the batch were not applied. " +
            "If the definition is still open, re-target it and resubmit.");
    }
}
