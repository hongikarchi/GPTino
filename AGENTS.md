# GPTino contributor instructions

- Keep the active Rhino and Grasshopper target explicit; never use an active-document fallback for writes.
- Reads may run concurrently against immutable snapshots. Live writes must pass through the single-writer broker.
- Wireify-derived behavior owns Python source, parameter typing, execution, and runtime errors.
- Cordyceps-derived behavior owns canvas topology, layout, groups, snapshots, and Rhino scene operations.
- Never commit `.3dm`, `.gh`, credentials, MCP configuration, runtime databases, or chat transcripts.
- Preserve attribution for ported implementation and update `docs/upstream-map.json`.
- Use document units and tolerances; do not hard-code geometry epsilons.
- A model's claim of success is not verification. Executor predicates decide success.

## Development safety boundary

- Keep all development writes under this repository. Reading outside it is allowed for diagnosis; writing outside it requires the user's approval for that external stage.
- Only the primary agent may edit files, start or stop processes, authenticate, install software, or push. Review agents are read-only and repository-local.
- Put local run state under `artifacts/dev-loop/<run-id>` and create `.gptino-owned-run` before starting child processes.
- Track exact child process IDs and start times. Stop only processes owned by that run; never use broad name-based termination.
- Preserve local test artifacts. Recursive cleanup is forbidden unless a canonical path is a marked descendant of the repository's artifact root and the cleanup is explicitly running in ephemeral CI.
- Do not use PowerShell's reserved `$HOME` variable as a scratch path. Do not use case variants such as `$home` either.
- Normalize repeated failures by stage, test or exception, core message, and exit code. Stop after three repair attempts with the same signature.
- Hidden or unavailable tool output and a model's success statement are not verification evidence.
- Login or network access, Rhino or Yak installation and GUI automation, and GitHub push are separate external stages; request approval once when each stage is reached.
