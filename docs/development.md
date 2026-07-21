# Development and packaging

## Toolchain

- Windows x64
- .NET SDK 8 (the pinned SDK is in `global.json`)
- Node.js 24 and npm
- Rhino 8.21+ for live plug-in validation and `Yak.exe`
- Codex CLI 0.144.6 (validated) or a protocol-compatible newer version

Clone the repository into any writable local directory and run the commands below
from that repository root.

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

For repeatable local verification, use the fixed-command development loop. Every
invocation creates a marked evidence directory under `artifacts/dev-loop/`, redirects
temporary and package-cache writes into that directory, records exact child PIDs and
start times, redacts secrets, and preserves stdout, stderr, timing, exit codes, and
normalized failure signatures:

```powershell
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage boundary
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage mcp
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage orchestrator
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage full
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage smoke
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage live-codex
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage package
dotnet run --project tools/GPTino.DevLoop/GPTino.DevLoop.csproj -- verify --stage rhino-live
```

The command accepts no arbitrary executable or script argument. Local evidence is
never automatically removed. Network/login, Rhino or Yak installation and GUI
automation, and GitHub push remain explicit external approval stages.

The `rhino-live` stage is the approved-machine acceptance test. It builds one local
Yak package, replaces the locally installed GPTino package, and starts exactly one
owned Rhino process. It refuses to run while any Rhino process is already open. The
fixed Desktop `Untitled.3dm` and `unnamed.gh` inputs are hashed and copied into the
marked run directory; only those copies are opened. The test drives real Codex
sessions to build a Grasshopper cylinder and Rhino sphere, independently inspects
slider values, sockets, wires, Python source/runtime, output bounds, Rhino object
identity and bounds, then verifies manual session ordering, conflict handling,
parallel reads, and terminal attachment. It stops only captured PID/start-time
identities, re-hashes the Desktop originals, preserves all evidence, and never logs
the generated API token. Because it changes local Yak installation state and opens
GUI processes, run it only after explicit approval.

When `rhino-live` enables the validated development data directory, the Rhino and
Grasshopper plug-ins also emit bounded `.gptino-diagnostic-*.json` startup
breadcrumbs there. These records identify document discovery and AgentHost lifecycle
events without recording API tokens or bridge secrets, and remain with the run evidence
when startup fails before the HTTP endpoint exists.

After a Release build, run the real AgentHost against the installed Codex CLI.
This checks the READY schema, API authentication, Codex model catalog, exact
Rhino-document binding, one-time panel nonce, and cookie reopen flow without
opening Rhino:

```powershell
./scripts/smoke-agenthost.ps1 -Configuration Release
```

To validate the exact staged self-contained executable, add
`-AgentHostExecutable artifacts/yak/GPTino/net8.0/agent/GPTino.AgentHost.exe`.

The default smoke starts the real Codex App Server and validates its model catalog,
so it is also an explicit network/login stage and must run in an approved, logged-in
user environment. It does not spend a model turn. Before packaging a release, run
the opt-in live turn against a logged-in native Codex executable:

```powershell
./scripts/smoke-agenthost.ps1 -Configuration Release `
  -CodexExecutable C:\path\to\codex.exe `
  -LiveCodexTurn
```

That path attaches a disposable, read-only Grasshopper bridge, requires exactly one
`snapshot_read`, and verifies an exact Korean UTF-8 assistant response containing the
unpredictable snapshot fingerprint. It rejects any mutation or second bridge request,
does not print the bridge secret or raw fingerprint, stops only its owned child
processes, and always preserves its marked runtime directory under
`artifacts/dev-loop/` as verification evidence.

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

The `rhino-live` development-loop stage automates the core acceptance path below;
the checklist remains useful for release-candidate visual inspection.

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
