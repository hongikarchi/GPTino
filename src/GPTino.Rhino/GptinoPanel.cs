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

    // NOTE: the WebView2 occlusion-disable flag (WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS) is set in
    // GptinoPlugIn.DisableWebViewOcclusionDetection at plugin load — earlier than any panel, which
    // is what the WebView2 loader requires. Do not duplicate it here.

    private const string SuppressFloatPromptSettingKey = "SuppressFloatingKeepVisiblePrompt";

    private readonly uint _documentSerial;
    private readonly WebView _webView;
    private readonly UITimer _readyTimer;
    private bool _navigated;
    private bool _wasVisible;
    private bool _wasForeground;
    private bool _wasFloating;
    private bool _floatPromptOpen;
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
        // Load/UnLoad fire on every re-parent — including floating the panel out of the dock and
        // docking it back — not just on create/close. The watchdog timer must therefore ALWAYS
        // restart on Load: the earlier "start only while not yet navigated" logic silently killed
        // the repaint watchdog the moment a navigated panel was floated, which is exactly when
        // cross-app occlusion (the white-panel case) needs it most. Navigation itself is left to
        // the tick: it navigates when not yet navigated and re-navigates on an endpoint change,
        // and never reloads an already-live page (re-parenting must not lose in-page state).
        Load += (_, _) =>
        {
            _readyTimer.Start();
            _wasForeground = IsOwnProcessForeground();
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
            // Re-observe the Rhino document each tick until it registers. At OnEndOpenDocument (and
            // in this panel's constructor) RhinoDoc.Path is often still empty / not fully qualified,
            // so the initial observation bails and the pair never registers — the panel then stays
            // on the waiting page until a Save finally supplies the authoritative path. Retrying
            // here lets registration (and the AgentHost spawn) happen shortly AFTER open, once the
            // path settles — so the user never has to Save to connect, and the spawn no longer
            // collides with a save's write+rename (the inherited-handle save failure). Idempotent:
            // once observed, repeat calls are a no-op via the host's changed-guard.
            GptinoRuntimeHost.Instance.ObserveRhinoDocument(_documentSerial);

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

        // Cross-app occlusion (another program's window fully covering a floated panel) never flips
        // Eto's Visible, so the hidden→visible edge above misses it entirely — that is exactly the
        // "reply in KakaoTalk, come back, panel is white" case. Key off the OS foreground state
        // instead: when a window of THIS process becomes foreground again (returning to Rhino or to
        // the floated panel itself, from any other app), force the same recomposition. This is the
        // only signal that reliably fires for whole-window occlusion by a foreign application.
        if (visible)
        {
            var foreground = IsOwnProcessForeground();
            if (foreground && !_wasForeground)
            {
                ForceRecomposite();
            }
            _wasForeground = foreground;

            // Docked→floating edge: when the panel lands in Rhino's floating container and Rhino
            // still hides floating windows on deactivate, offer (once per float) to flip that
            // option so the panel stays visible while the user works in other applications. A
            // restored session that starts out floating fires the edge on the first tick, which
            // is deliberate — the annoyance applies from the first app switch.
            var floating = IsHostedInFloatingContainer();
            if (floating && !_wasFloating)
            {
                MaybeOfferKeepVisibleOnFloat();
            }
            _wasFloating = floating;
        }
    }

    /// <summary>
    /// Strong recovery for the cross-app occlusion white-out: toggling the WebView's visibility
    /// drives the underlying WebView2 controller's IsVisible false→true, which resumes a renderer
    /// that a stuck occlusion tracker suspended — the case a bare 1px resize does not always cure.
    /// Used only on the foreground-regained edge, so the blink is at most one per app switch.
    /// </summary>
    private void ForceRecomposite()
    {
        _webView.Visible = false;
        _webView.Visible = true;
        NudgeRepaint();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// True when the current foreground window belongs to this process — i.e. the user just
    /// switched back to Rhino (or the floated panel) from another application.
    /// </summary>
    private static bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }
        _ = GetWindowThreadProcessId(foreground, out var owningProcessId);
        return owningProcessId == (uint)Environment.ProcessId;
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxLength);

    /// <summary>
    /// True when the panel currently lives in Rhino's floating dock container rather than the main
    /// frame. Rhino has no public "is this panel floating" API; the discriminator (verified live)
    /// is the top-level ancestor window's caption — the floating container is a top-level WPF
    /// window titled "TabPanelDockBarFloatingForm()", while a docked panel's root is the Rhino
    /// main frame (document title). Eto's NativeHandle resolves to a window inside whichever host
    /// currently parents the panel, so GA_ROOT lands on the right container after every re-parent.
    /// </summary>
    private bool IsHostedInFloatingContainer()
    {
        try
        {
            var handle = NativeHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            var root = GetAncestor(handle, GA_ROOT);
            if (root == IntPtr.Zero)
            {
                return false;
            }
            var text = new System.Text.StringBuilder(128);
            _ = GetWindowText(root, text, text.Capacity);
            return text.ToString().Contains("FloatingForm", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// On the docked→floating edge: when Rhino's HideFloatingWindowsOnDeactivate option is still
    /// on (the default — the floated panel vanishes whenever another app is active) and the user
    /// has not said "don't ask again", offer to flip it. Consent writes the option immediately via
    /// <see cref="RhinoAdvancedSettings"/>; decline just skips until the next float. The prompt is
    /// self-extinguishing: once the option is false the condition never holds again.
    /// </summary>
    private void MaybeOfferKeepVisibleOnFloat()
    {
        if (_floatPromptOpen || IsFloatPromptSuppressed())
        {
            return;
        }
        if (RhinoAdvancedSettings.TryGetHideFloatingWindowsOnDeactivate() != true)
        {
            return; // already kept visible, or accessors unavailable — nothing to offer
        }
        _floatPromptOpen = true;
        try
        {
            switch (ShowKeepVisibleDialog())
            {
                case FloatPromptChoice.KeepVisible:
                    if (!RhinoAdvancedSettings.TrySetHideFloatingWindowsOnDeactivate(false))
                    {
                        MessageBox.Show(
                            this,
                            "GPTino could not change the option automatically.\n\n" +
                            "To keep floating panels visible, open Rhino Options > Advanced, search for " +
                            "\"HideFloating\", and set Rhino.Options.Advanced.HideFloatingWindowsOnDeactivate to False.",
                            "GPTino",
                            MessageBoxType.Information);
                    }
                    break;
                case FloatPromptChoice.DontAskAgain:
                    SuppressFloatPrompt();
                    break;
            }
        }
        catch (Exception)
        {
            // A failed prompt must never take down the panel tick.
        }
        finally
        {
            _floatPromptOpen = false;
        }
    }

    private enum FloatPromptChoice
    {
        NotNow,
        KeepVisible,
        DontAskAgain,
    }

    private FloatPromptChoice ShowKeepVisibleDialog()
    {
        var choice = FloatPromptChoice.NotNow;
        var dialog = new Dialog<bool>
        {
            Title = "GPTino",
            Padding = new Padding(16),
            Resizable = false,
        };
        var keepVisible = new Button { Text = "Keep visible" };
        keepVisible.Click += (_, _) => { choice = FloatPromptChoice.KeepVisible; dialog.Close(true); };
        var notNow = new Button { Text = "Not now" };
        notNow.Click += (_, _) => { choice = FloatPromptChoice.NotNow; dialog.Close(false); };
        var dontAskAgain = new Button { Text = "Don't ask again" };
        dontAskAgain.Click += (_, _) => { choice = FloatPromptChoice.DontAskAgain; dialog.Close(false); };
        dialog.DefaultButton = keepVisible;
        dialog.AbortButton = notNow;
        dialog.Content = new StackLayout
        {
            Spacing = 12,
            Items =
            {
                new Label
                {
                    Text =
                        "Rhino hides floating panels while another application is active,\n" +
                        "so this panel will disappear whenever you switch apps.\n\n" +
                        "Keep it visible instead? This turns off the Rhino option\n" +
                        "\"HideFloatingWindowsOnDeactivate\" (affects all floating Rhino panels).",
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items = { keepVisible, notNow, dontAskAgain },
                },
            },
        };
        dialog.ShowModal(this);
        return choice;
    }

    private static bool IsFloatPromptSuppressed()
    {
        try
        {
            return GptinoPlugIn.Instance?.Settings.GetBool(SuppressFloatPromptSettingKey, false) ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SuppressFloatPrompt()
    {
        try
        {
            GptinoPlugIn.Instance?.Settings.SetBool(SuppressFloatPromptSettingKey, true);
        }
        catch (Exception)
        {
            // Best effort — without persistence the user is asked again on the next float.
        }
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
    /// The document states the waiting page explains. "unsaved" = a plain new document (a gentle
    /// File &gt; Save ask, no scary wording — every fresh Rhino start looks like this); "autosave"
    /// = the document sits on an autosave path (crash recovery / autosave copy opened directly)
    /// and is never observed; "readonly" = Rhino opened the file read-only (another instance or a
    /// stale .rhl lock), so saves will fail until the lock is cleared.
    /// </summary>
    private string DescribeDocumentState()
    {
        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(_documentSerial);
        if (document is null)
        {
            return "unknown";
        }
        var path = document.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "unsaved";
        }
        if (RhinoAutoSavePaths.IsAutoSavePath(path))
        {
            return "autosave";
        }
        if (document.IsReadOnly)
        {
            return "readonly";
        }
        return "saved";
    }

    private string ComputeWaitingKey() => $"{DescribeDocumentState()}|{GptinoRuntimeHost.Instance.Status}";

    private void ShowWaitingPage()
    {
        _waitingKey = ComputeWaitingKey();
        var status = WebUtility.HtmlEncode(GptinoRuntimeHost.Instance.Status);
        var documentState = DescribeDocumentState();
        var stateNotice = documentState switch
        {
            "autosave" => """
              <div class="notice">
                <b>This document is an autosave copy</b>, so GPTino cannot attach to it. Use
                <b>Save As</b> to give it a real path. Saving back to the original file path
                restores that file&#39;s previous GPTino sessions; a new path starts fresh (the old
                sessions stay on disk under the old path).
              </div>
              """,
            "readonly" => """
              <div class="notice">
                <b>Rhino opened this file read-only</b> — saves will fail with "temporary file"
                errors. This usually means another Rhino instance still has the file open, or a
                crash left a stale <b>.3dm.rhl</b> lock file next to it. Close other Rhino
                instances (check Task Manager), delete the stale <b>.rhl</b> file if the crash is
                long gone, then reopen the file.
              </div>
              """,
            // A plain new document: the one thing standing between the user and an attached
            // panel is a File > Save, so say exactly that — plainly, not as a warning box.
            // Grasshopper deliberately goes unmentioned: since the unconditional Rhino-only
            // target registration, a saved Rhino file alone boots the AgentHost, and the panel
            // itself (login gate, then the Model/Data tab CTAs) walks the user through the rest.
            "unsaved" => """
              <p>Save this Rhino document (<b>File &gt; Save</b>) and GPTino attaches by itself.</p>
              """,
            _ => string.Empty,
        };
        var html = $$"""
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <style>
                  body { font: 13px system-ui; margin: 20px; color: #c9d1d9; background: #161b22; }
                  small { color: #8b949e; }
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
                {{stateNotice}}
                <p><small>GPTino attaches to the saved Rhino file — no Grasshopper needed to start.
                Open a definition whenever canvas work begins; Rhino-side sessions run without one.</small></p>
                <small>Rhino document {{_documentSerial}}</small>
              </body>
            </html>
            """;
        _webView.LoadHtml(html, new Uri("http://127.0.0.1/"));
    }
}
