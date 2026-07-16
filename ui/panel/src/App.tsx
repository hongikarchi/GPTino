import { useEffect, useState } from "react";
import { ChatPane } from "./components/ChatPane";
import { Icon } from "./components/Icons";
import { OperationsPane } from "./components/OperationsPane";
import { SessionList } from "./components/SessionList";
import { useRuntime } from "./hooks/useRuntime";
import "./styles.css";

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

export default function App() {
  const { runtime, loading, error, demo, busyActions, actions } = useRuntime();
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
          </div>
        </div>

        <div className="runtime-summary">
          <div className="revision-block">
            <span>Live</span>
            <strong>r{runtime.revision}</strong>
          </div>
          <div className="revision-block">
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

      <main className="workspace-grid">
        <SessionList
          sessions={runtime.sessions}
          selectedId={selectedId}
          busyActions={busyActions}
          onSelect={setSelectedId}
          onCreate={() => {
            const suggested = `Session ${runtime.sessions.length + 1}`;
            const name = window.prompt("Name this GPTino session", suggested)?.trim();
            if (name) void actions.createSession(name);
          }}
          onReorder={actions.reorder}
          onShift={actions.shift}
          onPause={(id, paused) => void actions.pauseSession(id, paused)}
          onTerminal={(id) => void actions.openTerminal(id)}
        />
        <ChatPane
          session={selected}
          busyActions={busyActions}
          onMode={(mode) => selected && void actions.setMode(selected.id, mode)}
          onModel={(profile) => selected && void actions.setModel(selected.id, profile)}
          onSend={(content) => selected ? actions.sendMessage(selected.id, content) : undefined}
          onTerminal={() => selected && void actions.openTerminal(selected.id)}
        />
        <OperationsPane
          runtime={runtime}
          busyActions={busyActions}
          onPauseRuntime={(paused) => void actions.pauseRuntime(paused)}
          onStop={() => void actions.stopCurrent()}
        />
      </main>
    </div>
  );
}
