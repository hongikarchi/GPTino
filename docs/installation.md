# Installation

GPTino targets Windows x64 with Rhino 8.21 or newer. A normal installation is a
single Rhino Yak package containing the Rhino panel plug-in, Grasshopper bridge,
AgentHost, terminal client, and panel assets.

Wireify and Cordyceps are upstream design and implementation references. They are
not runtime prerequisites, and GPTino users should not install or configure either
plug-in separately. GPTino also creates its authenticated named-pipe endpoint
automatically; there is no port or shared-secret setup in the normal workflow.

## Prerequisites

- Windows x64 and Rhino 8.21+
- Codex CLI 0.144.6 (validated) or a protocol-compatible newer version
- a completed local Codex login (`codex login`)

The GPTino package includes a self-contained .NET runtime for AgentHost and the
terminal client. It does not bundle Codex CLI credentials, a Codex subscription,
Rhino, project `.3dm`/`.gh` files, or user session data.

## Install a published build

Once a release is published, find **GPTino** in Rhino's Package Manager and install
the desired version. Restart Rhino after installation so Rhino and Grasshopper can
discover both plug-ins from the same package.

For an unpublished development build, first create the `.yak` file on a Windows
machine with Rhino 8 installed:

```powershell
./scripts/build-package.ps1 -BuildYak
```

The command validates and tests the source before invoking Rhino's local `Yak.exe`.
Install the generated `.yak` with Rhino's Package Manager. Building a package never
uploads it; publishing requires a separate, deliberate Yak login and push.

## First run

1. Confirm `codex --version` and `codex login` work in a regular terminal.
2. Open the matching saved Rhino and Grasshopper documents.
3. Open the **GPTino** Rhino panel.
4. Create or select a session, then send a read-only request before the first edit.

GPTino intentionally has no separate Codex-login tab. It reuses the authenticated
user state created by `codex login` in a regular terminal. A session's **Terminal**
button opens that GPTino conversation; it is not a second Codex login shell.

The Rhino plug-in starts one AgentHost for the exact saved Rhino/Grasshopper file
pair in that Rhino process and connects it over a mutually authenticated named
pipe. The panel and session terminals talk only to AgentHost's ephemeral loopback
HTTP endpoint. GPTino does not expose a LAN port.

GPTino permits exactly one live AgentHost owner for a saved file pair, even
across Rhino processes. If the same pair is already open under GPTino elsewhere,
the second runtime stops before opening its queue or history; close the first
runtime before retrying.

Session state is stored beneath `%LOCALAPPDATA%\GPTino\projects\<fingerprint>`.
That directory is local user data; do not add it, Codex authentication files, or
bridge secrets to a project repository.

> [!IMPORTANT]
> GPTino is pre-release software. Keep ordinary backups and establish a verified
> baseline before allowing it to edit important Rhino or Grasshopper documents.

## Upgrade or remove

Use Rhino's Package Manager to upgrade or uninstall the package. Closing Rhino
stops its AgentHost and invalidates the in-memory bridge secret. Removing GPTino
does not delete the local session/history directory automatically; archive or remove
that data separately only after confirming it is no longer needed.
