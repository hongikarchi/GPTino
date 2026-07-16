# GPTino Yak layout

`packaging/yak/manifest.yml` is the source manifest. The release script copies it
into a generated staging directory, publishes the two out-of-process executables
as Windows x64 self-contained applications, and validates the package before Yak
is allowed to run. The package is one installable product; users do not install
standalone Wireify or Cordyceps packages.

```text
GPTino/
  manifest.yml
  LICENSE
  NOTICE
  THIRD_PARTY_NOTICES
  net8.0/
    GPTino.Rhino.rhp
    GPTino.Grasshopper.gha
    GPTino.*.dll
    agent/
      GPTino.AgentHost.exe
      GPTino.Terminal.exe
      GPTino.AgentHost.dll and self-contained runtime files
      wwwroot/
```

From the repository root:

```powershell
./scripts/build-package.ps1
```

If a machine's Windows PowerShell execution policy blocks repository scripts, use
the one-process form after reviewing the script; it does not change the machine's
persistent policy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-package.ps1
```

This produces a validated staging tree and a portable inspection archive under
`artifacts/`. It intentionally does not create or publish a `.yak` file by default.
On a machine with Rhino 8 installed, create the installable package explicitly:

```powershell
./scripts/build-package.ps1 -BuildYak
```

Yak is invoked with `--platform win`, so the package cannot be mistaken for a
cross-platform distribution.

The script refuses an output path outside this repository's `artifacts/` directory,
checks every required executable and panel asset, rejects common credential and
document file types, and never calls `yak push`. Do not manually place `.3dm`, `.gh`,
credentials, MCP configuration, session databases, or project history repositories
in the staging directory.
