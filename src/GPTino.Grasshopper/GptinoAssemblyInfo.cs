using System.Drawing;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;
using GPTino.WireifyAdapter;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

public sealed class GptinoAssemblyInfo : GH_AssemblyInfo
{
    public override string Name => "GPTino";

    public override Bitmap Icon => null!;

    public override string Description =>
        "Document-bound Grasshopper bridge for the GPTino modeling orchestrator.";

    public override Guid Id => new("d2b0c9b2-f64b-4be7-98fc-f01590e88ac8");

    public override string AuthorName => "GPTino contributors";

    public override string AuthorContact => "https://github.com/hongikarchi/GPTino";
}

public sealed class GptinoAssemblyPriority : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        DevelopmentDiagnosticTrace.TryWrite("Grasshopper", "priority-load-enter");
        try
        {
            GrasshopperDocumentCatalog.Initialize();
            GrasshopperSelectionWatcher.Start();
            var resolver = new ExplicitGrasshopperDocumentResolver();
            BridgeProcessHub.RegisterOperationHandler(
                new CordycepsCanvasBridgeOperationHandler(
                    new GrasshopperCanvasFoundationAdapter(resolver)));
            BridgeProcessHub.RegisterOperationHandler(
                new WireifyBridgeOperationHandler(
                    new GrasshopperPythonFoundationAdapter(resolver)));
            var documentCount = global::Grasshopper.Instances.DocumentServer.DocumentCount;
            var hasActiveCanvas = global::Grasshopper.Instances.ActiveCanvas is not null;
            DevelopmentDiagnosticTrace.TryWrite(
                "Grasshopper",
                "priority-load-ready",
                $"documents={documentCount};activeCanvas={hasActiveCanvas}");
            return GH_LoadingInstruction.Proceed;
        }
        catch (Exception exception)
        {
            GrasshopperSelectionWatcher.Stop();
            GrasshopperDocumentCatalog.Teardown();
            DevelopmentDiagnosticTrace.TryWriteException(
                "Grasshopper",
                "priority-load-failed",
                exception);
            throw;
        }
    }
}
