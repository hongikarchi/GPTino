using GPTino.BridgeContract;
using Rhino.Commands;
using Rhino.UI;

namespace GPTino.Rhino;

public sealed class GptinoOpenPanelCommand : Command
{
    public override string EnglishName => "GPTinoOpenPanel";

    protected override Result RunCommand(global::Rhino.RhinoDoc document, RunMode mode)
    {
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "open-panel-command",
            $"serial={document?.RuntimeSerialNumber ?? 0};saved={document is not null && !string.IsNullOrWhiteSpace(document.Path)}");
        if (document is null || string.IsNullOrWhiteSpace(document.Path))
        {
            global::Rhino.RhinoApp.WriteLine(
                "GPTino requires a saved Rhino document before opening its panel.");
            return Result.Nothing;
        }

        GptinoRuntimeHost.Instance.ObserveRhinoDocument(document.RuntimeSerialNumber);
        Panels.OpenPanel(typeof(GptinoPanel), true);
        var result = Panels.IsPanelVisible(typeof(GptinoPanel))
            ? Result.Success
            : Result.Failure;
        DevelopmentDiagnosticTrace.TryWrite("Rhino", "open-panel-result", result.ToString());
        return result;
    }
}
