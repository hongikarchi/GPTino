using System.Runtime.InteropServices;
using GPTino.BridgeContract;
using Rhino.PlugIns;
using Rhino.UI;

namespace GPTino.Rhino;

[Guid("b903e20d-1cb3-4d8e-b37d-9be263a678d4")]
public sealed class GptinoPlugIn : PlugIn
{
    private bool _closeDocumentSubscribed;
    private bool _endOpenDocumentSubscribed;
    private bool _endSaveDocumentSubscribed;
    private bool _selectionEventsSubscribed;

    public static GptinoPlugIn? Instance { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    public GptinoPlugIn()
    {
        Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        DevelopmentDiagnosticTrace.TryWrite("Rhino", "plugin-load-enter");
        var runtimeInitializationStarted = false;
        try
        {
            Panels.RegisterPanel(
                this,
                typeof(GptinoPanel),
                "GPTino",
                GetType().Assembly,
                string.Empty,
                PanelType.PerDoc);
            SubscribeDocumentEvents();
            runtimeInitializationStarted = true;
            GptinoRuntimeHost.Instance.RegisterRhinoSceneAdapter(
                new RhinoSceneFoundationAdapter(new ExplicitRhinoDocumentResolver()));
            GptinoRuntimeHost.Instance.Start(GetType().Assembly.Location);
            ObserveOpenDocuments();
            DevelopmentDiagnosticTrace.TryWrite("Rhino", "plugin-load-ready");
            return LoadReturnCode.Success;
        }
        catch (Exception exception)
        {
            UnsubscribeDocumentEvents();
            if (runtimeInitializationStarted)
            {
                TryDisposeRuntime("plugin-load-cleanup-failed");
            }
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "plugin-load-failed",
                exception);
            errorMessage =
                "GPTino failed to initialize. See the bounded development diagnostics for details.";
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        UnsubscribeDocumentEvents();
        try
        {
            TryDisposeRuntime("plugin-shutdown-cleanup-failed");
        }
        finally
        {
            base.OnShutdown();
        }
    }

    private void SubscribeDocumentEvents()
    {
        if (!_closeDocumentSubscribed)
        {
            global::Rhino.RhinoDoc.CloseDocument += OnCloseDocument;
            _closeDocumentSubscribed = true;
        }
        if (!_endSaveDocumentSubscribed)
        {
            global::Rhino.RhinoDoc.EndSaveDocument += OnEndSaveDocument;
            _endSaveDocumentSubscribed = true;
        }
        if (!_endOpenDocumentSubscribed)
        {
            global::Rhino.RhinoDoc.EndOpenDocument += OnEndOpenDocument;
            _endOpenDocumentSubscribed = true;
        }
        if (!_selectionEventsSubscribed)
        {
            global::Rhino.RhinoDoc.SelectObjects += OnSelectObjects;
            global::Rhino.RhinoDoc.DeselectObjects += OnDeselectObjects;
            global::Rhino.RhinoDoc.DeselectAllObjects += OnDeselectAllObjects;
            _selectionEventsSubscribed = true;
        }
    }

    private void UnsubscribeDocumentEvents()
    {
        if (_closeDocumentSubscribed)
        {
            try
            {
                global::Rhino.RhinoDoc.CloseDocument -= OnCloseDocument;
                _closeDocumentSubscribed = false;
            }
            catch (Exception exception)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "close-document-unsubscribe-failed",
                    exception);
            }
        }
        if (_endSaveDocumentSubscribed)
        {
            try
            {
                global::Rhino.RhinoDoc.EndSaveDocument -= OnEndSaveDocument;
                _endSaveDocumentSubscribed = false;
            }
            catch (Exception exception)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "end-save-document-unsubscribe-failed",
                    exception);
            }
        }
        if (_endOpenDocumentSubscribed)
        {
            try
            {
                global::Rhino.RhinoDoc.EndOpenDocument -= OnEndOpenDocument;
                _endOpenDocumentSubscribed = false;
            }
            catch (Exception exception)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "end-open-document-unsubscribe-failed",
                    exception);
            }
        }
        if (_selectionEventsSubscribed)
        {
            try
            {
                global::Rhino.RhinoDoc.SelectObjects -= OnSelectObjects;
                global::Rhino.RhinoDoc.DeselectObjects -= OnDeselectObjects;
                global::Rhino.RhinoDoc.DeselectAllObjects -= OnDeselectAllObjects;
                _selectionEventsSubscribed = false;
            }
            catch (Exception exception)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "selection-events-unsubscribe-failed",
                    exception);
            }
        }
    }

    private static void TryDisposeRuntime(string failureEvent)
    {
        try
        {
            GptinoRuntimeHost.Instance.Dispose();
        }
        catch (Exception exception)
        {
            DevelopmentDiagnosticTrace.TryWriteException("Rhino", failureEvent, exception);
        }
    }

    private static void OnCloseDocument(object? sender, global::Rhino.DocumentEventArgs args)
    {
        GptinoRuntimeHost.Instance.ForgetRhinoDocument(args.DocumentSerialNumber);
    }

    private static void OnEndSaveDocument(object? sender, global::Rhino.DocumentSaveEventArgs args)
    {
        if (!args.ExportSelected)
        {
            GptinoRuntimeHost.Instance.ObserveRhinoDocument(args.DocumentSerialNumber);
        }
    }

    private static void OnEndOpenDocument(object? sender, global::Rhino.DocumentOpenEventArgs args)
    {
        if (!args.Merge && !args.Reference)
        {
            GptinoRuntimeHost.Instance.ObserveRhinoDocument(args.DocumentSerialNumber);
        }
    }

    private static void OnSelectObjects(object? sender, global::Rhino.DocObjects.RhinoObjectSelectionEventArgs args)
    {
        GptinoRuntimeHost.Instance.NotifySelectionChanged(args.Document.RuntimeSerialNumber);
    }

    private static void OnDeselectObjects(object? sender, global::Rhino.DocObjects.RhinoObjectSelectionEventArgs args)
    {
        GptinoRuntimeHost.Instance.NotifySelectionChanged(args.Document.RuntimeSerialNumber);
    }

    private static void OnDeselectAllObjects(object? sender, global::Rhino.DocObjects.RhinoDeselectAllObjectsEventArgs args)
    {
        GptinoRuntimeHost.Instance.NotifySelectionChanged(args.Document.RuntimeSerialNumber);
    }

    private static void ObserveOpenDocuments()
    {
        foreach (var document in global::Rhino.RhinoDoc.OpenDocuments(false))
        {
            if (!string.IsNullOrWhiteSpace(document.Path))
            {
                GptinoRuntimeHost.Instance.ObserveRhinoDocument(document.RuntimeSerialNumber);
            }
        }
    }
}
