using System.Net;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;

namespace GPTino.Rhino;

/// <summary>A Rhino-created panel instance bound to exactly one runtime document serial.</summary>
[Guid("91ab786f-4437-457a-b04f-d0ddfe1d363b")]
public sealed class GptinoPanel : Panel
{
    private const string OpenGrasshopperScheme = "gptino";

    private readonly uint _documentSerial;
    private readonly WebView _webView;
    private readonly UITimer _readyTimer;
    private bool _navigated;
    private bool _wasVisible;
    private Uri? _navigatedBaseUri;
    private string? _waitingKey;

    public GptinoPanel(uint documentSerialNumber)
    {
        if (documentSerialNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerialNumber));
        }

        _documentSerial = documentSerialNumber;
        GptinoRuntimeHost.Instance.ObserveRhinoDocument(documentSerialNumber);
        _webView = new WebView();
        _webView.DocumentLoading += OnWebViewNavigating;
        Content = _webView;

        ShowWaitingPage();
        _readyTimer = new UITimer { Interval = 0.25 };
        // The timer navigates to the AgentHost UI once it is ready, then keeps ticking as a
        // lightweight repaint watchdog: the WebView2 native surface is lost when the docked
        // panel is hidden/occluded and only recomposites on a size change, so we nudge a
        // repaint on the hidden→visible edge (the same effect as the user's manual resize).
        _readyTimer.Elapsed += (_, _) => OnTimerTick();
        // Returning to Rhino and moving the pointer over the panel is the most reliable
        // "the user is looking at it now" signal Eto exposes for whole-app occlusion.
        MouseEnter += (_, _) => { if (_navigated) NudgeRepaint(); };
        Load += (_, _) =>
        {
            if (!TryNavigateToAgentHost())
            {
                _readyTimer.Start();
            }
        };
        UnLoad += (_, _) => _readyTimer.Stop();
        _readyTimer.Start();
    }

    public uint DocumentSerial => _documentSerial;

    private void OnTimerTick()
    {
        // Re-navigate whenever the live AgentHost endpoint changes: on first ready, and again after a
        // rebind (Save As / rename) spawns a fresh AgentHost on a new port. Without this the panel would
        // stay pinned to the old, now-dead port and show a connection-refused page.
        if (GptinoRuntimeHost.Instance.TryGetActivePanelBaseUri(_documentSerial, out var baseUri) &&
            !Equals(baseUri, _navigatedBaseUri))
        {
            TryNavigateToAgentHost();
            return;
        }
        if (!_navigated)
        {
            // Keep the waiting page's status and document-state explanation current while stuck.
            var waitingKey = ComputeWaitingKey();
            if (!string.Equals(waitingKey, _waitingKey, StringComparison.Ordinal))
            {
                ShowWaitingPage();
            }
            return;
        }
        var visible = Visible && Width > 0 && Height > 0;
        if (visible && !_wasVisible)
        {
            NudgeRepaint();
        }
        _wasVisible = visible;
    }

    private bool TryNavigateToAgentHost()
    {
        if (!GptinoRuntimeHost.Instance.TryGetPanelUri(_documentSerial, out var uri))
        {
            return false;
        }

        _webView.Url = uri;
        _navigated = true;
        _ = GptinoRuntimeHost.Instance.TryGetActivePanelBaseUri(_documentSerial, out var baseUri);
        _navigatedBaseUri = baseUri;
        return true;
    }

    /// <summary>
    /// Forces the native WebView2 child window to recomposite by toggling its size by 1px —
    /// the programmatic equivalent of the manual resize that recovers a blanked panel.
    /// Avoids Reload()/Url, which would discard the live in-page session state.
    /// </summary>
    private void NudgeRepaint()
    {
        var size = _webView.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            _webView.Invalidate(true);
            return;
        }
        _webView.Size = new Size(size.Width, size.Height - 1);
        _webView.Size = size;
        _webView.Invalidate(true);
    }

    private void OnWebViewNavigating(object? sender, WebViewLoadingEventArgs e)
    {
        if (e.Uri is { } uri && string.Equals(uri.Scheme, OpenGrasshopperScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            global::Rhino.RhinoApp.RunScript("_Grasshopper", echo: false);
        }
    }

    /// <summary>
    /// The document states the waiting page explains. "recovered" = pathless or sitting on an
    /// autosave path (crash recovery / autosave copy opened directly) — such documents are never
    /// observed, so without an explanation the panel looks stuck for no reason.
    /// </summary>
    private string DescribeDocumentState()
    {
        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(_documentSerial);
        if (document is null)
        {
            return "unknown";
        }
        var path = document.Path;
        if (string.IsNullOrWhiteSpace(path) || RhinoAutoSavePaths.IsAutoSavePath(path))
        {
            return "recovered";
        }
        return "saved";
    }

    private string ComputeWaitingKey() => $"{DescribeDocumentState()}|{GptinoRuntimeHost.Instance.Status}";

    private void ShowWaitingPage()
    {
        _waitingKey = ComputeWaitingKey();
        var status = WebUtility.HtmlEncode(GptinoRuntimeHost.Instance.Status);
        var recovered = DescribeDocumentState() == "recovered";
        var recoveredNotice = recovered
            ? """
              <div class="notice">
                <b>This document has no saved location</b> — it looks like a recovered or autosave
                copy, so GPTino cannot attach to it yet. Use <b>Save As</b> to give it a real path.
                Saving back to the original file path restores that file&#39;s previous GPTino sessions;
                a new path starts fresh (the old sessions stay on disk under the old path).
              </div>
              """
            : string.Empty;
        var html = $$"""
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <style>
                  body { font: 13px system-ui; margin: 20px; color: #c9d1d9; background: #161b22; }
                  small { color: #8b949e; }
                  a.cta {
                    display: inline-block; margin: 10px 0; padding: 6px 12px;
                    border: 1px solid #526334; border-radius: 6px;
                    color: #b7e166; text-decoration: none; background: rgba(183,225,102,0.06);
                  }
                  .notice {
                    margin: 10px 0; padding: 8px 12px;
                    border-left: 3px solid #e6b85c; border-radius: 0 6px 6px 0;
                    color: #ecd3a1; background: rgba(230,184,92,0.08); line-height: 1.5;
                  }
                </style>
              </head>
              <body>
                <h3>GPTino is starting</h3>
                <p>{{status}}</p>
                {{recoveredNotice}}
                <a class="cta" href="gptino://open-grasshopper">Open Grasshopper to start</a>
                <p><small>GPTino pairs one saved Rhino file with one saved Grasshopper file. Open (and save) a Grasshopper definition to begin.</small></p>
                <small>Rhino document {{_documentSerial}}</small>
              </body>
            </html>
            """;
        _webView.LoadHtml(html, new Uri("http://127.0.0.1/"));
    }
}
