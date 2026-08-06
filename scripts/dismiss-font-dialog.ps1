# Watchdog for headless dev-loop boots of REAL project files: production 3dm files routinely
# reference fonts the dev machine lacks, and Rhino's "Missing Fonts" dialog then blocks document
# open BEFORE the plugin ever sees a document — the runscript never runs and the AgentHost
# endpoint never appears. Rhino offers no CLI switch to suppress it, so the harness accepts the
# default substitutions the same way a human would: by pressing the dialog's default button.
# Scope-limited on purpose: only dialogs titled "Missing Fonts" owned by a Rhino process.
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'SilentlyContinue'
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class FontDialogDismiss {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumFunc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumFunc cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  delegate bool EnumFunc(IntPtr h, IntPtr l);
  const uint BM_CLICK = 0x00F5;
  public static string TryDismiss(HashSet<int> rhinoPids) {
    string outcome = null;
    EnumWindows((h, l) => {
      uint wpid; GetWindowThreadProcessId(h, out wpid);
      if (!rhinoPids.Contains((int)wpid) || !IsWindowVisible(h)) return true;
      var cls = new StringBuilder(64); GetClassName(h, cls, 64);
      if (cls.ToString() != "#32770") return true;
      var title = new StringBuilder(128); GetWindowText(h, title, 128);
      if (title.ToString() != "Missing Fonts") return true;
      IntPtr ok = IntPtr.Zero, fallback = IntPtr.Zero;
      EnumChildWindows(h, (c, l2) => {
        var ccls = new StringBuilder(64); GetClassName(c, ccls, 64);
        if (!ccls.ToString().Contains("Button")) return true;
        var text = new StringBuilder(64); GetWindowText(c, text, 64);
        var t = text.ToString().Replace("&", "");
        if (t == "OK" || t == "확인") ok = c;      // OK / 확인
        else if (fallback == IntPtr.Zero) fallback = c;
        return true;
      }, IntPtr.Zero);
      var target = ok != IntPtr.Zero ? ok : fallback;
      if (target != IntPtr.Zero) {
        SendMessage(target, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        outcome = "clicked " + (ok != IntPtr.Zero ? "OK" : "first button") + " on Missing Fonts (pid " + wpid + ")";
        return false;
      }
      return true;
    }, IntPtr.Zero);
    return outcome;
  }
}
"@
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $pids = [System.Collections.Generic.HashSet[int]]::new()
    Get-Process Rhino -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add($_.Id) }
    if ($pids.Count -gt 0) {
        $hit = [FontDialogDismiss]::TryDismiss($pids)
        if ($hit) { Write-Host $hit }
    }
    Start-Sleep -Milliseconds 1500
}
