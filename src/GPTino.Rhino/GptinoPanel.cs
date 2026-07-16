using System.Net;
using System.Runtime.InteropServices;
using Eto.Forms;

namespace GPTino.Rhino;

/// <summary>A Rhino-created panel instance bound to exactly one runtime document serial.</summary>
[Guid("91ab786f-4437-457a-b04f-d0ddfe1d363b")]
public sealed class GptinoPanel : Panel
{
    private readonly uint _documentSerial;
    private readonly WebView _webView;
    private readonly UITimer _readyTimer;

    public GptinoPanel(uint documentSerialNumber)
    {
        if (documentSerialNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerialNumber));
        }

        _documentSerial = documentSerialNumber;
        GptinoRuntimeHost.Instance.ObserveRhinoDocument(documentSerialNumber);
        _webView = new WebView();
        Content = _webView;

        ShowWaitingPage();
        _readyTimer = new UITimer { Interval = 0.25 };
        _readyTimer.Elapsed += (_, _) => TryNavigateToAgentHost();
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

    private bool TryNavigateToAgentHost()
    {
        if (!GptinoRuntimeHost.Instance.TryGetPanelUri(_documentSerial, out var uri))
        {
            return false;
        }

        _readyTimer.Stop();
        _webView.Url = uri;
        return true;
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
                </style>
              </head>
              <body>
                <h3>GPTino is starting</h3>
                <p>{{status}}</p>
                <small>Rhino document {{_documentSerial}}</small>
              </body>
            </html>
            """;
        _webView.LoadHtml(html, new Uri("http://127.0.0.1/"));
    }
}
