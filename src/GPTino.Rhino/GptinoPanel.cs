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
        if (!_navigated)
        {
            TryNavigateToAgentHost();
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

    private void ShowWaitingPage()
    {
        var status = WebUtility.HtmlEncode(GptinoRuntimeHost.Instance.Status);
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
                </style>
              </head>
              <body>
                <h3>GPTino is starting</h3>
                <p>{{status}}</p>
                <a class="cta" href="gptino://open-grasshopper">Open Grasshopper to start</a>
                <p><small>GPTino pairs one saved Rhino file with one saved Grasshopper file. Open (and save) a Grasshopper definition to begin.</small></p>
                <small>Rhino document {{_documentSerial}}</small>
              </body>
            </html>
            """;
        _webView.LoadHtml(html, new Uri("http://127.0.0.1/"));
    }
}
