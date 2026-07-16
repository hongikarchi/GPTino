using System.Runtime.InteropServices;
using Rhino.PlugIns;
using Rhino.UI;

namespace GPTino.Rhino;

[Guid("b903e20d-1cb3-4d8e-b37d-9be263a678d4")]
public sealed class GptinoPlugIn : PlugIn
{
    public static GptinoPlugIn? Instance { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    public GptinoPlugIn()
    {
        Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            Panels.RegisterPanel(
                this,
                typeof(GptinoPanel),
                "GPTino",
                GetType().Assembly,
                string.Empty,
                PanelType.PerDoc);
            global::Rhino.RhinoDoc.CloseDocument += OnCloseDocument;
            GptinoRuntimeHost.Instance.RegisterRhinoSceneAdapter(
                new RhinoSceneFoundationAdapter(new ExplicitRhinoDocumentResolver()));
            GptinoRuntimeHost.Instance.Start(GetType().Assembly.Location);
            return LoadReturnCode.Success;
        }
        catch (Exception exception)
        {
            errorMessage = $"GPTino failed to initialize: {exception.Message}";
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        global::Rhino.RhinoDoc.CloseDocument -= OnCloseDocument;
        GptinoRuntimeHost.Instance.Dispose();
        base.OnShutdown();
    }

    private static void OnCloseDocument(object? sender, global::Rhino.DocumentEventArgs args)
    {
        GptinoRuntimeHost.Instance.ForgetRhinoDocument(args.DocumentSerialNumber);
    }
}
