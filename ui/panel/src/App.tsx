import { useEffect, useRef, useState } from "react";
import { ArchiveBrowser } from "./components/ArchiveBrowser";
import { ChatPane } from "./components/ChatPane";
import { Icon } from "./components/Icons";
import { SessionCanvas } from "./components/SessionCanvas";
import { useRuntime } from "./hooks/useRuntime";
import type { CodexAuth, GrasshopperDocInfo } from "./types";
import "./styles.css";

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
  const { runtime, models, loading, error, demo, busyActions, actions } = useRuntime();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [conflictsOpen, setConflictsOpen] = useState(false);
  const [archiveOpen, setArchiveOpen] = useState(false);
  const [newSessionOpen, setNewSessionOpen] = useState(false);
  const newSessionAnchorRef = useRef<HTMLDivElement | null>(null);

  // Esc or a press outside the + Session button / popover closes it.
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
    document.addEventListener("pointerdown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [newSessionOpen]);

  useEffect(() => {
    if (!runtime?.sessions.length) return;
    if (!selectedId || !runtime.sessions.some(({ id }) => id === selectedId)) {
      setSelectedId(runtime.sessions[0].id);
    }
  }, [runtime, selectedId]);

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

  const selected = runtime.sessions.find(({ id }) => id === selectedId);
  const ghDocs = runtime.grasshopperDocs != null && runtime.grasshopperDocs.length > 0 ? runtime.grasshopperDocs : null;
  const multiGh = ghDocs != null && ghDocs.length > 1;

  return (
    <div className="app-shell">
      <header className="document-header">
        <div className="brand-mark" title="GPTino — Rhino orchestration">G</div>

        <div className="project-lockup">
          <div className="project-name-row">
            <h1>{runtime.projectName}</h1>
            {demo ? <span className="demo-chip">Demo</span> : null}
          </div>
          <div className="file-pair">
            <span title={runtime.rhinoFile}>R <b>{shortFile(runtime.rhinoFile)}</b></span>
            <Icon name="chevron" />
            {multiGh ? (
              <span title={ghDocs.map(({ file }) => file).join("\n")}>GH <b>×{ghDocs.length}</b></span>
            ) : (
              <span title={runtime.grasshopperFile}>GH <b>{shortFile(runtime.grasshopperFile)}</b></span>
            )}
            {runtime.contextFolder ? (
              <button
                type="button"
                className="context-chip"
                title={`Project context folder (rules.md, MEMORY.md) — click to copy path\n${runtime.contextFolder}`}
                onClick={() => {
                  void navigator.clipboard?.writeText(runtime.contextFolder!).catch(() => undefined);
                }}
              >
                context
              </button>
            ) : null}
          </div>
        </div>

        <div className="runtime-summary">
          <div
            className="revision-block"
            title={
              "Live: document revision — increments each time a committed change is applied to the live Rhino/Grasshopper document.\n" +
              "Git: managed history commit — a git-backed provenance trail of every verified change, reviewable and rollbackable."
            }
          >
            <b>live</b> r{runtime.revision} · <b>git</b> {runtime.gitRevision == null ? "—" : `#${runtime.gitRevision}`}
          </div>
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

      <section className="canvas-row" aria-label="Session graph">
        <SessionCanvas
          runtime={runtime}
          selectedId={selectedId}
          onSelect={setSelectedId}
          onReorder={actions.reorder}
          onPauseToggle={(id, paused) => void actions.pauseSession(id, paused)}
        />
        <div className="canvas-toolbar">
          <div className="new-session-anchor" ref={newSessionAnchorRef}>
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
                suggestedName={`Session ${runtime.sessions.length + 1}`}
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
          <div className="canvas-global-actions">
            <button
              type="button"
              className={`secondary-button ${runtime.paused ? "resume" : ""}`}
              onClick={() => void actions.pauseRuntime(!runtime.paused)}
              disabled={busyActions.has("pause-runtime")}
              title={runtime.paused ? "Resume every session" : "Pause every session at its next safe boundary"}
            >
              <Icon name={runtime.paused ? "play" : "pause"} />
              {runtime.paused ? "Resume all" : "Pause all"}
            </button>
            <button
              type="button"
              className="danger-button"
              onClick={() => void actions.stopCurrent()}
              disabled={!runtime.writer || busyActions.has("stop-current")}
              title="Stop the live single-writer transaction at its next safe boundary"
            >
              <Icon name="stop" />
              Stop current
            </button>
          </div>
        </div>
      </section>

      <main className="chat-region">
        <ChatPane
          session={selected}
          conflicts={runtime.conflicts}
          models={models}
          grasshopperDocs={ghDocs}
          busyActions={busyActions}
          onMode={(mode) => selected && void actions.setMode(selected.id, mode)}
          onModel={(profile) => selected && void actions.setModel(selected.id, profile, selected.pinnedModel ?? null)}
          onPinModel={(model) => selected && void actions.setModel(selected.id, selected.modelProfile, model)}
          onTarget={(grasshopperDoc) => selected && void actions.setSessionTarget(selected.id, grasshopperDoc)}
          onSend={(content, attachments) => selected ? actions.sendMessage(selected.id, content, attachments) : undefined}
        />
      </main>

      {archiveOpen ? (
        <ArchiveBrowser
          onClose={() => setArchiveOpen(false)}
          listArchive={actions.listArchive}
          readMessages={actions.readArchiveMessages}
        />
      ) : null}
    </div>
  );
}
