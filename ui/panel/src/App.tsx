import { useEffect, useState } from "react";
import { ChatPane } from "./components/ChatPane";
import { Icon } from "./components/Icons";
import { SessionCanvas } from "./components/SessionCanvas";
import { useRuntime } from "./hooks/useRuntime";
import type { CodexAuth } from "./types";
import "./styles.css";

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

// LLM sign-in indicator (blue = signed in, red = signed out / CLI missing).
// Codex only for now — a Claude backend is deferred, so a second provider
// indicator would slot in right next to this one when that lands.
function LlmAuthIndicator({ auth, busy, onLogin }: { auth: CodexAuth; busy: boolean; onLogin: () => void }) {
  const loggedIn = auth.status === "logged-in";
  const detail =
    auth.detail ??
    (loggedIn
      ? "Signed in"
      : auth.status === "cli-missing"
        ? "Codex CLI not found"
        : "Signed out — click to log in");
  const body = (
    <>
      <span className="llm-light" />
      <div>
        <strong>Codex</strong>
        <span>{detail}</span>
      </div>
    </>
  );
  if (loggedIn) {
    return (
      <div className={`llm-auth llm-${auth.status}`} title={`Codex — ${detail}`}>
        {body}
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
      {body}
    </button>
  );
}

export default function App() {
  const { runtime, models, loading, error, demo, busyActions, actions } = useRuntime();
  const [selectedId, setSelectedId] = useState<string | null>(null);

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

  return (
    <div className="app-shell">
      <header className="document-header">
        <div className="brand-lockup">
          <div className="brand-mark">G</div>
          <div>
            <strong>GPTino</strong>
            <span>Rhino orchestration</span>
          </div>
        </div>

        <div className="project-lockup">
          <div className="project-name-row">
            <h1>{runtime.projectName}</h1>
            {demo ? <span className="demo-chip">Demo</span> : null}
          </div>
          <div className="file-pair">
            <span title={runtime.rhinoFile}>R <b>{shortFile(runtime.rhinoFile)}</b></span>
            <Icon name="chevron" />
            <span title={runtime.grasshopperFile}>GH <b>{shortFile(runtime.grasshopperFile)}</b></span>
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
            title="Live document revision — increments each time a committed change is applied to the live Rhino/Grasshopper document."
          >
            <span>Live</span>
            <strong>r{runtime.revision}</strong>
          </div>
          <div
            className="revision-block"
            title="Managed history commit — GPTino keeps a git-backed provenance trail of every verified change so it can be reviewed or rolled back."
          >
            <span>Git</span>
            <strong>{runtime.gitRevision === undefined ? "—" : `#${runtime.gitRevision}`}</strong>
          </div>
          <div className={`connection-state health-${runtime.health}`}>
            <span className="connection-light" />
            <div>
              <strong>{runtime.health}</strong>
              <span>{runtime.healthDetail ?? "Document runtime"}</span>
            </div>
          </div>
          {runtime.codexAuth ? (
            <LlmAuthIndicator
              auth={runtime.codexAuth}
              busy={busyActions.has("login-terminal")}
              onLogin={() => void actions.openLoginTerminal()}
            />
          ) : null}
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
        <div className="conflict-banner" role="alert">
          <Icon name="warning" />
          <span>
            {runtime.conflicts.length} resource conflict{runtime.conflicts.length > 1 ? "s" : ""} — {runtime.conflicts[0].title}
          </span>
        </div>
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
          <button
            type="button"
            className="new-session-button"
            onClick={() => {
              const suggested = `Session ${runtime.sessions.length + 1}`;
              const name = window.prompt("Name this GPTino session", suggested)?.trim();
              if (name) void actions.createSession(name);
            }}
            disabled={busyActions.has("create-session")}
          >
            <span>+</span> Session
          </button>
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
          models={models}
          busyActions={busyActions}
          onMode={(mode) => selected && void actions.setMode(selected.id, mode)}
          onModel={(profile) => selected && void actions.setModel(selected.id, profile, selected.pinnedModel ?? null)}
          onPinModel={(model) => selected && void actions.setModel(selected.id, selected.modelProfile, model)}
          onSend={(content) => selected ? actions.sendMessage(selected.id, content) : undefined}
        />
      </main>
    </div>
  );
}
