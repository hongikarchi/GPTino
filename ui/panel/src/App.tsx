import { useCallback, useEffect, useRef, useState } from "react";
import { ArchiveBrowser } from "./components/ArchiveBrowser";
import { AuditCard } from "./components/AuditCard";
import { ChatPane } from "./components/ChatPane";
import { CuratorActions } from "./components/CuratorActions";
import { DataView } from "./components/DataView";
import { DeletedSessions } from "./components/DeletedSessions";
import { Icon } from "./components/Icons";
import { NoGrasshopper } from "./components/NoGrasshopper";
import { SessionCanvas } from "./components/SessionCanvas";
import { ToastStack } from "./components/Toast";
import { useRuntime } from "./hooks/useRuntime";
import { useSessionCompletion } from "./hooks/useSessionCompletion";
import { ensureNotificationPermission } from "./notifications";
import type { CodexAuth, GrasshopperDocInfo, RhinoAuditKind } from "./types";
import "./styles.css";

const NOTIFY_ASKED_KEY = "gptino.notify.asked";

// Request notification permission at most once per browser, on the first message
// send — a real user gesture, and the exact moment the user starts work they may
// later want to be pinged about. A declined answer is remembered by the browser.
function requestNotifyPermissionOnce() {
  try {
    if (localStorage.getItem(NOTIFY_ASKED_KEY)) return;
    localStorage.setItem(NOTIFY_ASKED_KEY, "1");
  } catch {
    // localStorage can be unavailable in a locked-down WebView; fall through and ask.
  }
  void ensureNotificationPermission();
}

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

// LLM sign-in indicator (blue = signed in, red = signed out / CLI missing).
// Codex only for now — a Claude backend is deferred, so a second provider
// indicator would slot in right next to this one when that lands.
// When signed in the detail line collapses into the tooltip; the extra text is
// only worth its space while it is a call to action.
function LlmAuthIndicator({ auth, busy, onLogin }: { auth: CodexAuth; busy: boolean; onLogin: () => void }) {
  const loggedIn = auth.status === "logged-in";
  const detail =
    auth.detail ??
    (loggedIn
      ? "Signed in"
      : auth.status === "cli-missing"
        ? "Codex CLI not found"
        : "Signed out — click to log in");
  if (loggedIn) {
    return (
      <div className={`llm-auth llm-${auth.status}`} title={`Codex — ${detail}`}>
        <span className="llm-light" />
        <div>
          <strong>Codex</strong>
        </div>
      </div>
    );
  }
  return (
    <button
      type="button"
      className={`llm-auth llm-${auth.status}`}
      onClick={onLogin}
      disabled={busy}
      title={`Codex — ${detail}. Click to open a terminal and run 'codex login'.`}
    >
      <span className="llm-light" />
      <div>
        <strong>Codex</strong>
        <span>{detail}</span>
      </div>
    </button>
  );
}

// Popover replacing the old window.prompt for naming a new session. When more
// than one GH doc is registered it also asks which document the session should
// write to; with zero or one doc the doc list is hidden and behavior matches
// the old name-only prompt.
function NewSessionPopover({
  suggestedName,
  docs,
  defaultDocId,
  busy,
  onCreate,
}: {
  suggestedName: string;
  docs: GrasshopperDocInfo[];
  defaultDocId?: string;
  busy: boolean;
  onCreate(name: string, grasshopperDoc?: string): void;
}) {
  const [name, setName] = useState(suggestedName);
  const [docId, setDocId] = useState<string | undefined>(
    docs.some((doc) => doc.id === defaultDocId) ? defaultDocId : docs[0]?.id,
  );
  const showDocs = docs.length > 1;

  return (
    <form
      className="new-session-popover"
      onSubmit={(event) => {
        event.preventDefault();
        const trimmed = name.trim();
        if (trimmed) onCreate(trimmed, showDocs ? docId : undefined);
      }}
    >
      <label className="popover-label" htmlFor="new-session-name">
        Session name
      </label>
      <input
        id="new-session-name"
        type="text"
        autoFocus
        value={name}
        onChange={(event) => setName(event.target.value)}
        onFocus={(event) => event.target.select()}
      />
      {showDocs ? (
        <fieldset className="popover-docs">
          <legend className="popover-label">Grasshopper document</legend>
          {docs.map((doc) => (
            <label className="popover-doc" key={doc.id} title={doc.file}>
              <input
                type="radio"
                name="new-session-doc"
                checked={docId === doc.id}
                onChange={() => setDocId(doc.id)}
              />
              <span>{shortFile(doc.file)}</span>
            </label>
          ))}
        </fieldset>
      ) : null}
      <button type="submit" className="popover-create" disabled={busy || !name.trim()}>
        Create session
      </button>
    </form>
  );
}

export default function App() {
  const { runtime, serverRuntime, models, loading, error, demo, busyActions, actions } = useRuntime();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Completion deep-links (toasts, OS notifications) must be tab-aware: a curator completion
  // switches to the curator tab instead of selecting it on the Model rail. The handler needs
  // per-render session data, so the hook gets a stable ref-dispatching callback.
  const openSessionRef = useRef<(id: string) => void>(() => {});
  const openSessionStable = useCallback((id: string) => openSessionRef.current(id), []);
  const completion = useSessionCompletion(serverRuntime, selectedId, openSessionStable);
  const [conflictsOpen, setConflictsOpen] = useState(false);
  const [archiveOpen, setArchiveOpen] = useState(false);
  const [trashOpen, setTrashOpen] = useState(false);
  const [newSessionOpen, setNewSessionOpen] = useState(false);
  const [canvasCollapsed, setCanvasCollapsed] = useState(() => {
    try {
      return localStorage.getItem("gptino.canvasCollapsed") === "1";
    } catch {
      return false;
    }
  });
  const toggleCanvas = () =>
    setCanvasCollapsed((collapsed) => {
      const next = !collapsed;
      try {
        localStorage.setItem("gptino.canvasCollapsed", next ? "1" : "0");
      } catch {
        // localStorage may be unavailable; the toggle still works for this session.
      }
      return next;
    });
  // Open audit approval card on the curator tab (null = closed). The nonce forces a fresh scan
  // when the same preset is clicked again (the card remounts).
  const [auditKind, setAuditKind] = useState<RhinoAuditKind | null>(null);
  const [auditNonce, setAuditNonce] = useState(0);
  // [Model | Curator | Data] view switch inside the one panel: tabs are presentation only — the
  // curator underneath is an ordinary session flowing through the same broker, and the data view
  // projects the same runtime snapshot everything else reads.
  const [tab, setTab] = useState<"model" | "curator" | "data">(() => {
    try {
      const stored = localStorage.getItem("gptino.tab");
      return stored === "curator" || stored === "data" ? stored : "model";
    } catch {
      return "model";
    }
  });
  const switchTab = (next: "model" | "curator" | "data") => {
    setTab(next);
    try {
      localStorage.setItem("gptino.tab", next);
    } catch {
      // localStorage may be unavailable; the switch still works for this session.
    }
  };
  const newSessionAnchorRef = useRef<HTMLDivElement | null>(null);

  // Esc or a press outside the + Session button / popover closes it. Capture
  // phase, because canvas nodes call stopPropagation() on pointerdown — a
  // bubble listener would never see those presses.
  useEffect(() => {
    if (!newSessionOpen) return;
    const handlePointerDown = (event: PointerEvent) => {
      const anchor = newSessionAnchorRef.current;
      if (anchor && event.target instanceof Node && !anchor.contains(event.target)) {
        setNewSessionOpen(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setNewSessionOpen(false);
    };
    document.addEventListener("pointerdown", handlePointerDown, true);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown, true);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [newSessionOpen]);

  useEffect(() => {
    // Auto-select only among Model-tab sessions: the resident curator is never a default
    // selection (it would hijack a fresh project's first view).
    const modelSessions = runtime?.sessions.filter((session) => session.role !== "curator") ?? [];
    if (!modelSessions.length) return;
    if (!selectedId || !modelSessions.some(({ id }) => id === selectedId)) {
      setSelectedId(modelSessions[0].id);
    }
  }, [runtime, selectedId]);

  // Viewing a session clears its unread dot. Keyed on serverRuntime too so a
  // completion that lands on the already-selected session clears on the next snapshot.
  const { markSeen } = completion;
  useEffect(() => {
    if (selectedId) markSeen(selectedId);
  }, [selectedId, serverRuntime, markSeen]);
  // The curator tab is its own "viewing" surface: while it is active, the curator session's
  // completions are seen even though selectedId stays Model-only.
  useEffect(() => {
    if (tab !== "curator") return;
    const curator = runtime?.sessions.find((session) => session.role === "curator");
    if (curator) markSeen(curator.id);
  }, [tab, runtime, serverRuntime, markSeen]);

  if (loading) {
    return (
      <main className="boot-screen">
        <div className="brand-mark large">G</div>
        <div className="boot-copy">
          <strong>Attaching to Rhino</strong>
          <span>Loading the active document runtime…</span>
        </div>
        <div className="boot-line"><span /></div>
      </main>
    );
  }

  if (!runtime) {
    return (
      <main className="boot-screen error-screen">
        <div className="brand-mark large">G</div>
        <div className="boot-copy">
          <strong>GPTino is not connected</strong>
          <span>{error ?? "Open a saved Rhino and Grasshopper file, then attach this panel."}</span>
        </div>
        <button type="button" className="secondary-button" onClick={() => window.location.reload()}>
          Retry connection
        </button>
      </main>
    );
  }

  const modelSessions = runtime.sessions.filter((session) => session.role !== "curator");
  const curatorSession = runtime.sessions.find((session) => session.role === "curator");
  const selected = modelSessions.find(({ id }) => id === selectedId);
  const ghDocs = runtime.grasshopperDocs != null && runtime.grasshopperDocs.length > 0 ? runtime.grasshopperDocs : null;
  // No definition open is a normal state, not a failure: the panel comes up on a saved Rhino file
  // alone and Curator is fully usable. Only Model and Data need a canvas. The legacy single-doc
  // server sends grasshopperFile without grasshopperDocs, so either signal counts.
  const hasGrasshopper = ghDocs != null || runtime.grasshopperFile != null;
  const modelUnread = modelSessions.some((session) => completion.unseen.has(session.id));
  const curatorUnread = curatorSession != null && completion.unseen.has(curatorSession.id);
  // A definition pointing at deleted Rhino objects emits empty data with no error — the one
  // data-flow fact that earns an attention dot rather than waiting to be looked up.
  const brokenReferences = (runtime.dataFlow ?? []).reduce(
    (total, flow) => total + flow.missingReferenceCount,
    0,
  );
  const curatorBusy =
    curatorSession?.status === "working" ||
    curatorSession?.status === "drafting" ||
    curatorSession?.status === "verifying" ||
    curatorSession?.status === "queued" ||
    curatorSession?.paused === true;
  const openSession = (id: string) => {
    if (curatorSession && id === curatorSession.id) {
      switchTab("curator");
    } else {
      switchTab("model");
      setSelectedId(id);
    }
    completion.markSeen(id);
  };
  openSessionRef.current = openSession;

  return (
    <div className="app-shell">
      <header className="document-header">
        <div className="brand-mark" title="GPTino — Rhino orchestration">G</div>

        <div className="project-lockup">
          <div className="project-name-row">
            <h1 title={runtime.rhinoFile}>{runtime.projectName}</h1>
            {demo ? <span className="demo-chip">Demo</span> : null}
          </div>
        </div>

        <div className="runtime-summary">
          <div
            className={`connection-state health-${runtime.health}`}
            title={runtime.healthDetail ?? "Document runtime"}
          >
            <span className="connection-light" />
            <strong>{runtime.health}</strong>
          </div>
          {runtime.codexAuth ? (
            <LlmAuthIndicator
              auth={runtime.codexAuth}
              busy={busyActions.has("login-terminal")}
              onLogin={() => void actions.openLoginTerminal()}
            />
          ) : null}
        </div>

        <div className="session-toolbar">
          <div className="toolbar-group">
            {/* Graph/+Session act on the Model tab's canvas and rail; showing them on another
                tab would mutate invisible state. Deleted/Past sessions stay global. */}
            {tab === "model" && hasGrasshopper ? (
            <button
              type="button"
              className="secondary-button"
              onClick={toggleCanvas}
              aria-expanded={!canvasCollapsed}
              title={canvasCollapsed ? "Show the session graph" : "Collapse the session graph"}
            >
              {canvasCollapsed ? `▸ Graph (${modelSessions.length})` : "▾ Graph"}
            </button>
            ) : null}
            <div className="new-session-anchor" ref={newSessionAnchorRef} hidden={tab !== "model" || !hasGrasshopper}>
              <button
                type="button"
                className="new-session-button"
                onClick={() => setNewSessionOpen((open) => !open)}
                disabled={busyActions.has("create-session")}
                aria-expanded={newSessionOpen}
              >
                <span>+</span> Session
              </button>
              {newSessionOpen ? (
                <NewSessionPopover
                  suggestedName={`Session ${modelSessions.length + 1}`}
                  docs={ghDocs ?? []}
                  defaultDocId={selected?.boundGrasshopperDocId ?? undefined}
                  busy={busyActions.has("create-session")}
                  onCreate={(name, grasshopperDoc) => {
                    setNewSessionOpen(false);
                    void actions.createSession(name, grasshopperDoc);
                  }}
                />
              ) : null}
            </div>
          </div>
          <div className="toolbar-group">
            <button
              type="button"
              className="secondary-button"
              onClick={() => setTrashOpen(true)}
              title="Restore or permanently remove deleted sessions"
            >
              Deleted
            </button>
            <button
              type="button"
              className="history-button"
              onClick={() => setArchiveOpen(true)}
              title="Browse what earlier GPTino sessions did — every project data root on this machine, read-only"
            >
              <Icon name="history" />
              Past sessions
            </button>
          </div>
        </div>
      </header>

      {error ? (
        <div className="error-banner" role="status">
          <Icon name="warning" />
          <span>{error}</span>
          <button type="button" onClick={() => window.location.reload()}>Reconnect</button>
        </div>
      ) : null}

      {runtime.paused ? (
        <div className="pause-banner" role="status">
          <Icon name="pause" />
          <span>Executor paused — active transaction will stop at its next safe boundary.</span>
          <button type="button" onClick={() => void actions.pauseRuntime(false)}>Resume all</button>
        </div>
      ) : null}

      {runtime.conflicts.length > 0 ? (
        <>
          <button
            type="button"
            className="conflict-banner"
            role="alert"
            aria-expanded={conflictsOpen}
            onClick={() => setConflictsOpen((open) => !open)}
            title={conflictsOpen ? "Hide conflict details" : "Show conflict details"}
          >
            <Icon name="warning" />
            <span>
              {runtime.conflicts.length} resource conflict{runtime.conflicts.length > 1 ? "s" : ""} — {runtime.conflicts[0].title}
            </span>
            <Icon name="chevron" className={`banner-caret ${conflictsOpen ? "open" : ""}`} width={13} height={13} />
          </button>
          {conflictsOpen ? (
            <div className="conflict-drawer">
              {runtime.conflicts.map((conflict) => {
                const sessionTitles = conflict.sessionIds
                  .map((id) => runtime.sessions.find((session) => session.id === id)?.title ?? id)
                  .join(" ↔ ");
                return (
                  <div className="conflict-card" key={conflict.id}>
                    <div className="conflict-icon"><Icon name="warning" /></div>
                    <div>
                      <strong>{conflict.title}</strong>
                      <p className="conflict-problem">{conflict.detail}</p>
                      {conflict.resolution ? (
                        <p className="conflict-solution"><b>Solution</b> — {conflict.resolution}</p>
                      ) : null}
                      <div className="conflict-meta">
                        {conflict.resource ? <span>{conflict.resource}</span> : null}
                        {sessionTitles ? <span>{sessionTitles}</span> : null}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          ) : null}
        </>
      ) : null}

      {/* Model = build it, Curator = tidy it, Data = see what flows between the documents. Each
          tab owns exactly one thing, so nothing curator- or data-shaped leaks into Model. */}
      <nav className="tab-bar" aria-label="Panel view">
        <div className="segmented view-tabs">
          <button
            type="button"
            className={tab === "model" ? "active" : ""}
            aria-pressed={tab === "model"}
            onClick={() => switchTab("model")}
            title="Grasshopper modeling sessions"
          >
            Model
            {modelUnread && tab !== "model" ? <span className="tab-dot" aria-label="Unread activity" /> : null}
          </button>
          <button
            type="button"
            className={tab === "curator" ? "active" : ""}
            aria-pressed={tab === "curator"}
            onClick={() => switchTab("curator")}
            disabled={!curatorSession}
            title={curatorSession ? "Document care — audits, cleanup, one-shot batches" : "No curator session yet"}
          >
            Curator
            {curatorUnread && tab !== "curator" ? <span className="tab-dot" aria-label="Unread activity" /> : null}
          </button>
          <button
            type="button"
            className={tab === "data" ? "active" : ""}
            aria-pressed={tab === "data"}
            onClick={() => switchTab("data")}
            title="What Grasshopper references from Rhino and what it bakes back"
          >
            Data
            {brokenReferences > 0 && tab !== "data" ? (
              <span className="tab-dot warning" aria-label="Broken references" />
            ) : null}
          </button>
        </div>
      </nav>

      {tab === "model" && hasGrasshopper && !canvasCollapsed ? (
        <section className="canvas-row" aria-label="Session graph">
          <SessionCanvas
            runtime={runtime}
            selectedId={selectedId}
            unseenIds={completion.unseen}
            onSelect={setSelectedId}
            onReorder={actions.reorder}
            onOpenDataFlow={() => switchTab("data")}
          />
        </section>
      ) : null}

      {/* Model and Data are the only Grasshopper-dependent tabs. Without a definition they show
          the CTA in place of their own body — the panel itself is up, and Curator is fully usable. */}
      {tab === "data" ? (
        <main className="chat-region data-region">
          {hasGrasshopper ? (
            <DataView
              docs={ghDocs}
              summaries={runtime.dataFlow ?? []}
              unattributedBakeCount={runtime.unattributedBakeCount ?? 0}
              rhinoFile={runtime.rhinoFile}
              grasshopperFile={runtime.grasshopperFile ?? ""}
              getDetail={actions.getDataFlowDetail}
            />
          ) : (
            <NoGrasshopper
              detail="This tab shows what a definition references from Rhino and what it bakes back, so it needs one open."
              curatorAvailable={curatorSession != null}
              onOpenCurator={() => switchTab("curator")}
            />
          )}
        </main>
      ) : null}

      {/* The two chat regions stay MOUNTED and toggle via `hidden`: unmounting a ChatPane would
          silently discard its composer draft and staged attachments on every tab switch. The Data
          region holds no draft, so it unmounts — and re-reads the ledger on every visit. */}
      <main className="chat-region" hidden={tab !== "model"}>
          {!hasGrasshopper ? (
            <NoGrasshopper
              detail="Modeling sessions drive a Grasshopper definition, so they need one open and saved."
              curatorAvailable={curatorSession != null}
              onOpenCurator={() => switchTab("curator")}
            />
          ) : (
          <ChatPane
            key={selected?.id ?? "none"}
            session={selected}
            conflicts={runtime.conflicts}
            models={models}
            limits={runtime.codexLimits ?? null}
            grasshopperDocs={ghDocs}
            busyActions={busyActions}
            onMode={(mode) => selected && void actions.setMode(selected.id, mode)}
            onModel={(profile) => selected && void actions.setModel(selected.id, profile, selected.pinnedModel ?? null)}
            onPinModel={(model) => selected && void actions.setModel(selected.id, selected.modelProfile, model)}
            onGoal={(enabled) => selected && void actions.setGoal(selected.id, enabled)}
            onTarget={(grasshopperDoc) => selected && void actions.setSessionTarget(selected.id, grasshopperDoc)}
            onSend={(content, attachments) => {
              if (!selected) return undefined;
              requestNotifyPermissionOnce();
              return actions.sendMessage(selected.id, content, attachments);
            }}
            onResume={() => selected && void actions.pauseSession(selected.id, false)}
            onDelete={() => {
              if (!selected) return;
              const deletedId = selected.id;
              void actions.deleteSession(deletedId);
              if (selectedId === deletedId) setSelectedId(null);
            }}
            onStopEdit={() => (selected ? actions.retractLast(selected.id) : Promise.resolve(null))}
          />
          )}
      </main>
      <main className="chat-region curator-region" hidden={tab !== "curator"}>
          <CuratorActions
            busy={curatorBusy}
            onRun={(prompt) => {
              if (!curatorSession) return;
              requestNotifyPermissionOnce();
              void actions.sendMessage(curatorSession.id, prompt);
            }}
            onAudit={(kind) => {
              setAuditKind(kind);
              setAuditNonce((nonce) => nonce + 1);
            }}
          />
          {auditKind ? (
            <AuditCard
              key={`${auditKind}-${auditNonce}`}
              kind={auditKind}
              runAudit={actions.getAudit}
              onFocus={actions.focusObjects}
              // Open solids are REPORT ONLY: the findings carry no proposed fix, because
              // rebuilding a shell is a modelling decision with several valid answers.
              approvable={auditKind !== "openBrepEdges"}
              busy={curatorBusy}
              onClose={() => setAuditKind(null)}
              onApprove={async (result, approved, keepFirst) => {
                if (!curatorSession) {
                  throw new Error("No curator session is available to apply the fixes.");
                }
                // Approve-what-you-saw, NARROWLY: the grant covers only the objects an approved
                // fix may write. The endpoint-fix anchor and the duplicate copy the user chose to
                // KEEP stay uncovered — a confused fix targeting them is denied, not approved.
                const items = approved.flatMap((finding) => {
                  if (finding.kind === "nearMissEndpoints") {
                    return [{ objectId: finding.objectIds[1], fingerprint: finding.fingerprints[1] ?? "" }];
                  }
                  if (finding.kind === "nearDuplicates") {
                    const remove = (keepFirst[finding.findingId] ?? true) ? 1 : 0;
                    return [
                      { objectId: finding.objectIds[remove], fingerprint: finding.fingerprints[remove] ?? "" },
                    ];
                  }
                  // Purge subkinds: unused blocks and empty layers are document-table entries,
                  // not user geometry, so they carry no object grant. Only quarantining a bad
                  // object writes to an object the user may have made.
                  if (finding.kind === "badObject") {
                    return [{ objectId: finding.objectIds[0], fingerprint: finding.fingerprints[0] ?? "" }];
                  }
                  if (finding.kind === "unusedBlockDefinition" || finding.kind === "emptyLayer") {
                    return [];
                  }
                  return finding.objectIds.map((objectId, index) => ({
                    objectId,
                    fingerprint: finding.fingerprints[index] ?? "",
                  }));
                });
                // Blocks and empty layers are document-table entries, not user geometry: an
                // approval covering zero objects is a legitimate outcome, and minting an empty
                // grant would just 400.
                const grant = items.length > 0 ? await actions.mintApprovalGrant(items) : null;
                const lines = approved
                  .map((finding) => {
                    if (finding.kind === "nearDuplicates") {
                      const keep = (keepFirst[finding.findingId] ?? true) ? 0 : 1;
                      const remove = keep === 0 ? 1 : 0;
                      return `- ${finding.findingId}: DELETE duplicate ${finding.objectIds[remove]} and KEEP ${finding.objectIds[keep]} (deleteRhinoObject with the audited fingerprint; the grant covers only the deleted copy).`;
                    }
                    if (finding.kind === "nearMissEndpoints") {
                      const ends = finding.endIndices ?? [];
                      return `- ${finding.findingId}: heal the endpoint gap (${finding.measure}) via fixRhinoEndpointPair: anchorObjectId=${finding.objectIds[0]}, anchorEnd=${ends[0] ?? 0}, moveObjectId=${finding.objectIds[1]}, moveEnd=${ends[1] ?? 0}; declare the anchor in the readSet with its audited fingerprint.`;
                    }
                    if (finding.kind === "unusedBlockDefinition") {
                      return `- ${finding.findingId}: purge block ${finding.objectIds[0]} — put it in the SINGLE purgeTableEntries operation's entries array as {"table":"block","id":"${finding.objectIds[0]}"}, and declare a matching rhinoBlockDefinition write for it.`;
                    }
                    if (finding.kind === "emptyLayer") {
                      return `- ${finding.findingId}: deleteRhinoLayer layerId=${finding.objectIds[0]} with expectedFingerprint=${finding.fingerprints[0]} (rhino_layers gives the current one if it drifted).`;
                    }
                    if (finding.kind === "badObject") {
                      return `- ${finding.findingId}: QUARANTINE ${finding.objectIds[0]} (expectedFingerprint=${finding.fingerprints[0]}) — do NOT delete it. If the layer "GPTino::Quarantine" does not exist yet, create it with ensureRhinoLayer in a FIRST ChangeSet, read its id back with rhino_layers, then moveObjectsToLayer in a second ChangeSet (the move needs the layer's real id, which only exists after the layer is created).`;
                    }
                    return `- ${finding.findingId}: ${finding.detail}`;
                  })
                  .join("\n");
                requestNotifyPermissionOnce();
                await actions.sendMessage(
                  curatorSession.id,
                  `The user APPROVED these ${result.kind} findings on the approval card.\n${lines}\n` +
                    (grant
                      ? `Approval grant: ${grant.grantId} (bound to the audited fingerprints of the objects ` +
                        `above). Set "approvalGrantId": "${grant.grantId}" inside the changeSet object of ` +
                        `change_submit.\n`
                      : `No approval grant is needed — these findings touch document-table entries, not the ` +
                        `user's geometry.\n`) +
                    `Apply exactly these fixes now, then re-run rhino_audit and report the verified result.`,
                );
              }}
            />
          ) : null}
          <ChatPane
            key={curatorSession?.id ?? "curator-none"}
            session={curatorSession}
            conflicts={runtime.conflicts}
            models={models}
            limits={runtime.codexLimits ?? null}
            grasshopperDocs={null}
            busyActions={busyActions}
            onMode={(mode) => curatorSession && void actions.setMode(curatorSession.id, mode)}
            onModel={(profile) =>
              curatorSession && void actions.setModel(curatorSession.id, profile, curatorSession.pinnedModel ?? null)}
            onPinModel={(model) =>
              curatorSession && void actions.setModel(curatorSession.id, curatorSession.modelProfile, model)}
            onGoal={(enabled) => curatorSession && void actions.setGoal(curatorSession.id, enabled)}
            onTarget={() => undefined}
            onSend={(content, attachments) => {
              if (!curatorSession) return undefined;
              requestNotifyPermissionOnce();
              return actions.sendMessage(curatorSession.id, content, attachments);
            }}
            onResume={() => curatorSession && void actions.pauseSession(curatorSession.id, false)}
            onDelete={() => undefined /* the resident curator is not deletable; the server 409s anyway */}
            onStopEdit={() =>
              curatorSession ? actions.retractLast(curatorSession.id) : Promise.resolve(null)}
          />
      </main>

      {archiveOpen ? (
        <ArchiveBrowser
          onClose={() => setArchiveOpen(false)}
          listArchive={actions.listArchive}
          readMessages={actions.readArchiveMessages}
          importSession={actions.importArchiveSession}
        />
      ) : null}

      {trashOpen ? (
        <DeletedSessions
          onClose={() => setTrashOpen(false)}
          list={actions.listDeleted}
          onRestore={actions.restoreSession}
          onPurge={actions.purgeSession}
        />
      ) : null}

      <ToastStack
        toasts={completion.toasts}
        onDismiss={completion.dismissToast}
        onOpen={openSession}
      />
    </div>
  );
}
