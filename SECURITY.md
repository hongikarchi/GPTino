# Security policy

Use GitHub private vulnerability reporting. Do not open a public issue containing
credentials, document data, or a write-broker bypass.

## Trust boundaries

- Codex sessions never receive the file-pair bridge secret or a broker-issued
  writer lease.
- The panel never receives MCP secrets or writer leases.
- Model-authored operations remain proposals until target, fingerprint, scope,
  reversibility, and postconditions are validated.
- Raw Wireify/Cordyceps MCP servers are not part of the default runtime path.

Session chat and draft directories are isolated by GPTino's API and dynamic
tools, but Codex threads run under the same local user account. They are not a
confidentiality boundary against local shell or filesystem access. Keep
credentials outside project, chat, and artifact directories.

The local bridge uses protocol v2 mutual, role-separated HMAC authentication
bound to endpoint, nonces, and peer IDs; it validates handshake correlation,
times out stalled handshakes, and uses current-user-only first-instance named
pipes on Windows. HMAC authenticates the connection handshake—it is not
per-frame encryption or a per-frame MAC. Processes already running as the same
OS user remain inside the local trust boundary.

Until the first stable release, only the latest tagged pre-release is supported.
