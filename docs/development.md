# Development and packaging

## Toolchain

- Windows x64
- .NET SDK 8 (the pinned SDK is in `global.json`)
- Node.js 24 and npm
- Rhino 8.21+ for live plug-in validation and `Yak.exe`
- Codex CLI 0.144.4 (validated) or a protocol-compatible newer version

Clone the repository anywhere writable. The canonical local location used for this
project is `C:\Users\user\Desktop\GPTino`.

## Build order

The panel must be built before AgentHost. AgentHost includes `ui/panel/dist` as its
published `wwwroot`; reversing the order can produce a working host with no panel.

```powershell
Set-Location ui/panel
npm ci
npm run test
npm run build
Set-Location ../..

dotnet restore GPTino.sln
dotnet build GPTino.sln -c Release --no-restore
dotnet test GPTino.sln -c Release --no-build --no-restore
```

After a Release build, run the real AgentHost against the installed Codex CLI.
This checks the READY schema, API authentication, Codex model catalog, exact
Rhino-document binding, one-time panel nonce, and cookie reopen flow without
opening Rhino:

```powershell
./scripts/smoke-agenthost.ps1 -Configuration Release
```

To validate the exact staged self-contained executable, add
`-AgentHostExecutable artifacts/yak/GPTino/net8.0/agent/GPTino.AgentHost.exe`.

To inspect the pinned Wireify, Cordyceps, and SkillMeld sources used for design
comparison, run `./scripts/fetch-references.ps1`. They are checked out under the
ignored `.references/` directory and are not runtime dependencies.

## Assemble one distribution

The packaging script runs the panel tests/build, .NET restore/build/tests, publishes
AgentHost and Terminal as Windows x64 self-contained executables, and stages the
Rhino/Grasshopper binaries in one Yak layout:

```powershell
./scripts/build-package.ps1
```

If local execution policy blocks the script, review it and use
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-package.ps1`.
This bypass applies only to that process and does not alter the machine policy.

Generated output:

```text
artifacts/
  GPTino-<version>-yak-stage.zip
  GPTino-<version>-yak-stage-contents.txt
  yak/GPTino/
    manifest.yml
    LICENSE
    NOTICE
    THIRD_PARTY_NOTICES
    legal/
      DIRECT_MIT_LICENSES.txt
      CORDYCEPS_MIT_LICENSE.txt
      LIBGIT2_LICENSE.txt
      SCHEDULER_LICENSE.txt
      DOTNET_THIRD_PARTY_NOTICES.txt
    net8.0/
      GPTino.Rhino.rhp
      GPTino.Grasshopper.gha
      GPTino.*.dll
      agent/
        GPTino.AgentHost.exe
        GPTino.Terminal.exe
        wwwroot/
```

The ZIP is an inspection/CI artifact, not an installable Yak package. On a machine
with Rhino 8 installed, ask the script to invoke Yak:

```powershell
./scripts/build-package.ps1 -BuildYak
```

If Rhino is installed in a nonstandard location, pass `-YakExe` explicitly. The
script marks the package as Windows-only, never logs in to Yak, and never pushes a
package. A release upload must remain a separate, deliberate operation after
inspecting the package contents.

Useful iteration switches are `-SkipPanelBuild`, `-SkipRestore`,
`-SkipSolutionBuild`, and `-SkipTests`. They assume the corresponding prior outputs
are current; the final package validation still runs. `-OutputRoot` is intentionally
restricted to `artifacts/` or one of its descendants so a clean rebuild cannot erase
an unrelated directory.

## Live validation checklist

Automated builds cannot prove Rhino UI-thread behavior. Before publishing a version:

1. Install the locally built `.yak` into the supported Rhino version.
2. Open one saved `.3dm` and its intended `.gh` file, then open the GPTino panel.
3. Confirm AgentHost readiness and explicit document registration.
4. Search the component catalog and list/filter Rhino objects from a session.
5. Start two sessions and verify reads can overlap.
6. Submit two conflicting edits and verify only one owns the writer lease.
7. Create a primitive, transform it, then run a contiguous Python source/schema/execute
   sequence and confirm each request uses the preceding after-fingerprint.
8. Inspect a real Point JSON payload, confirm `{}` and a Point/Brep type mismatch fail
   before any earlier batch write, and confirm valid generic upsert succeeds.
9. Open each session's terminal and confirm it attaches to the same session history.
10. Close Rhino and confirm AgentHost and session terminals stop or disconnect cleanly.

Never put test `.3dm`/`.gh` files, `.mcp.json`, Codex authentication, bridge secrets,
SQLite runtime files, or managed project history into the package staging directory.
