import { useCallback, useEffect, useRef, useState } from "react";
import { ArchiveBrowser } from "./components/ArchiveBrowser";
import { ChatPane } from "./components/ChatPane";
import { DataView } from "./components/DataView";
import { DeletedSessions } from "./components/DeletedSessions";
import { Icon } from "./components/Icons";
import { NoGrasshopper } from "./components/NoGrasshopper";
import { SessionCanvas } from "./components/SessionCanvas";
import { ToastStack } from "./components/Toast";
import { useRuntime } from "./hooks/useRuntime";
import { useSessionCompletion } from "./hooks/useSessionCompletion";
import { ensureNotificationPermission } from "./notifications";
import type { GrasshopperDocInfo } from "./types";
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

// A provider status chip in the header: a colored dot (blue = connected, red = not) plus the
// provider name. When disconnected AND actionable it is a button whose click runs the reconnect
// action (Codex → login terminal; Grasshopper → the _Grasshopper command); otherwise a static div.
// The connection axis (was a separate "connected" chip) is now just the dot colour on each provider.
function StatusChip({
  label,
  connected,
  detail,
  actionable = false,
  busy = false,
  onClick,
}: {
  label: string;
  connected: boolean;
  detail: string;
  actionable?: boolean;
  busy?: boolean;
  onClick?: () => void;
}) {
  const className = `status-chip ${connected ? "status-on" : "status-off"}`;
  if (connected || !actionable) {
    return (
      <div className={className} title={`${label} — ${detail}`}>
        <span className="status-dot" />
        <strong>{label}</strong>
      </div>
    );
  }
  return (
    <button type="button" className={className} onClick={onClick} disabled={busy} title={`${label} — ${detail}`}>
      <span className="status-dot" />
      <strong>{label}</strong>
    </button>
  );
}

// The Rhino-side WebView intercepts this scheme and runs the _Grasshopper command; there is no HTTP
// request behind it. Shared by the Grasshopper status chip and the (still-available) empty-state CTA.
const OPEN_GRASSHOPPER_URL = "gptino://open-grasshopper";

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
  const { runtime, serverRuntime, models, loading, error, demo, busyActions, language, actions } = useRuntime();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Completion deep-links (toasts, OS notifications) select the session on the Model rail. The
  // handler needs per-render session data, so the hook gets a stable ref-dispatching callback.
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
  // [Model | Data] view switch inside the one panel: tabs are presentation only. Neither is a
  // mode and neither gates what a session may do — Data just projects the same runtime snapshot
  // everything else reads.
  const [tab, setTab] = useState<"model" | "data">(() => {
    try {
      return localStorage.getItem("gptino.tab") === "data" ? "data" : "model";
    } catch {
      return "model";
    }
  });
  const switchTab = (next: "model" | "data") => {
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
    const sessions = runtime?.sessions ?? [];
    if (!sessions.length) return;
    if (!selectedId || !sessions.some(({ id }) => id === selectedId)) {
      setSelectedId(sessions[0].id);
    }
  }, [runtime, selectedId]);

  // Viewing a session clears its unread dot. Keyed on serverRuntime too so a
  // completion that lands on the already-selected session clears on the next snapshot.
  const { markSeen } = completion;
  useEffect(() => {
    if (selectedId) markSeen(selectedId);
  }, [selectedId, serverRuntime, markSeen]);

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

  const modelSessions = runtime.sessions;
  const selected = modelSessions.find(({ id }) => id === selectedId);
  const ghDocs = runtime.grasshopperDocs != null && runtime.grasshopperDocs.length > 0 ? runtime.grasshopperDocs : null;
  // No definition open is a normal state, not a failure: the panel comes up on a saved Rhino file
  // alone and Rhino-side work still runs. Only Model and Data need a canvas. The legacy single-doc
  // server sends grasshopperFile without grasshopperDocs, so either signal counts.
  const hasGrasshopper = ghDocs != null || runtime.grasshopperFile != null;
  const modelUnread = modelSessions.some((session) => completion.unseen.has(session.id));
  // A definition pointing at deleted Rhino objects emits empty data with no error — the one
  // data-flow fact that earns an attention dot rather than waiting to be looked up.
  const brokenReferences = (runtime.dataFlow ?? []).reduce(
    (total, flow) => total + flow.missingReferenceCount,
    0,
  );
  const openSession = (id: string) => {
    switchTab("model");
    setSelectedId(id);
    completion.markSeen(id);
  };
  openSessionRef.current = openSession;

  return (
    <div className="app-shell">
      <header className="document-header">
        {/* The Rhino-runtime connection (was a standalone "connected" chip) is folded into the
            brand mark's tint: green when connected, amber/red when degraded/disconnected. */}
        <div
          className={`brand-mark health-${runtime.health}`}
          title={runtime.healthDetail ?? `Rhino runtime — ${runtime.health}`}
        >
          G
        </div>

        <div className="project-lockup">
          <div className="project-name-row">
            <h1 title={runtime.rhinoFile}>{runtime.projectName}</h1>
            {demo ? <span className="demo-chip">Demo</span> : null}
          </div>
        </div>

        <div className="runtime-summary">
          {/* Prose language for GPTino's answers. UI labels (Effort, Plan/Auto, tool names)
              stay English on purpose — they are vocabulary, not prose. */}
          <button
            type="button"
            className="language-toggle"
            title={
              language === "ko"
                ? "GPTino의 답변 언어: 한국어 (클릭하면 English) — 다음 턴부터 적용"
                : "GPTino answers in English (click for 한국어) — applies from the next turn"
            }
            onClick={() => void actions.setLanguage(language === "ko" ? "en" : "ko")}
          >
            {language === "ko" ? "한" : "ENG"}
          </button>
          {runtime.codexAuth ? (
            <StatusChip
              label="Codex"
              connected={runtime.codexAuth.status === "logged-in"}
              detail={
                runtime.codexAuth.detail ??
                (runtime.codexAuth.status === "logged-in"
                  ? "Signed in"
                  : runtime.codexAuth.status === "cli-missing"
                    ? "Codex CLI not found — click to open a terminal and run 'codex login'"
                    : "Signed out — click to open a terminal and run 'codex login'")
              }
              actionable
              busy={busyActions.has("login-terminal")}
              onClick={() => void actions.openLoginTerminal()}
            />
          ) : null}
          <StatusChip
            label="Grasshopper"
            connected={hasGrasshopper}
            detail={hasGrasshopper ? "Definition open" : "No definition open — click to open Grasshopper"}
            actionable={!hasGrasshopper}
            onClick={() => {
              window.location.href = OPEN_GRASSHOPPER_URL;
            }}
          />
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
            <div className="new-session-anchor" ref={newSessionAnchorRef} hidden={tab !== "model"}>
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

      {/* Model = the sessions you talk to, Data = what flows between the documents. Two views of
          the same runtime; neither is a mode, and neither gates what a session may do. */}
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

      {/* Model and Data both need a definition. Without one they show the CTA in place of their
          own body — the panel itself is up, and Rhino-side work still runs. */}
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
              onSelectRhino={(objectIds) => void actions.focusObjects(objectIds, "select")}
            />
          ) : (
            <NoGrasshopper detail="This tab shows what a definition references from Rhino and what it bakes back, so it needs one open." />
          )}
        </main>
      ) : null}

      {/* The chat region stays MOUNTED and toggles via `hidden`: unmounting a ChatPane would
          silently discard its composer draft and staged attachments on every tab switch. The Data
          region holds no draft, so it unmounts — and re-reads the ledger on every visit.
          No Grasshopper gate here: chatting is allowed without a definition open (Rhino-side work,
          planning) — a missing definition shows only as the red Grasshopper dot in the header. */}
      <main className="chat-region" hidden={tab !== "model"}>
          <ChatPane
            key={selected?.id ?? "none"}
            session={selected}
            conflicts={runtime.conflicts}
            models={models}
            limits={runtime.codexLimits ?? null}
            grasshopperDocs={ghDocs}
            busyActions={busyActions}
            onModel={(profile) => selected && void actions.setModel(selected.id, profile, selected.pinnedModel ?? null)}
            onPinModel={(model) => selected && void actions.setModel(selected.id, selected.modelProfile, model)}
            onAnswerGoal={(answer) => selected && void actions.answerGoal(selected.id, answer)}
            onAnswerApproval={(answer) => selected && void actions.answerApproval(selected.id, answer)}
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
            onFocus={actions.focusObjects}
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
