using GPTino.CordycepsAdapter;
using GPTino.BridgeContract;
using GPTino.WireifyAdapter;

namespace GPTino.Grasshopper;

/// <summary>
/// Connects concrete GH adapters to the process bridge. The adapters still resolve every call by
/// DocumentRuntime; this method does not select an active canvas.
/// </summary>
public static class GrasshopperBridgeBootstrap
{
    public static void Configure(
        IWireifyDocumentAdapter wireifyAdapter,
        ICordycepsCanvasAdapter canvasAdapter)
    {
        ArgumentNullException.ThrowIfNull(wireifyAdapter);
        ArgumentNullException.ThrowIfNull(canvasAdapter);
        BridgeProcessHub.RegisterOperationHandler(
            new WireifyBridgeOperationHandler(wireifyAdapter));
        BridgeProcessHub.RegisterOperationHandler(
            new CordycepsCanvasBridgeOperationHandler(canvasAdapter));
    }
}
