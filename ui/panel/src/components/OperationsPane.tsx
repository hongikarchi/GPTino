import type { GptinoSession, RuntimeConflict, RuntimeState } from "../types";
import { Icon } from "./Icons";

interface OperationsPaneProps {
  runtime: RuntimeState;
  busyActions: Set<string>;
  onPauseRuntime(paused: boolean): void;
  onStop(): void;
}

const sessionTitle = (sessions: GptinoSession[], id: string) =>
  sessions.find((session) => session.id === id)?.title ?? "Unknown session";

function ConflictCard({ conflict, sessions }: { conflict: RuntimeConflict; sessions: GptinoSession[] }) {
  return (
    <article className="conflict-card">
      <div className="conflict-icon"><Icon name="warning" /></div>
      <div>
        <strong>{conflict.title}</strong>
        <p>{conflict.detail}</p>
        <span>{conflict.sessionIds.map((id) => sessionTitle(sessions, id)).join(", ")}</span>
      </div>
    </article>
  );
}

export function OperationsPane({
  runtime,
  busyActions,
  onPauseRuntime,
  onStop,
}: OperationsPaneProps) {
  return (
    <aside className="operations-pane" aria-label="Execution status">
      <div className="pane-heading operations-heading">
        <div>
          <span className="eyebrow">Single writer</span>
          <h2>Operations</h2>
        </div>
        <span className={`writer-indicator ${runtime.writer ? "live" : "idle"}`}>
          {runtime.writer ? "Live" : "Idle"}
        </span>
      </div>

      <section className="writer-card">
        {runtime.writer ? (
          <>
            <div className="writer-topline">
              <span className="pulse-ring"><span /></span>
              <span>{sessionTitle(runtime.sessions, runtime.writer.sessionId)}</span>
            </div>
            <h3>{runtime.writer.label}</h3>
            <p>{runtime.writer.phase}</p>
            <div className="progress-track" aria-label={`${runtime.writer.progress ?? 0}% complete`}>
              <span style={{ width: `${runtime.writer.progress ?? 0}%` }} />
            </div>
            <div className="writer-detail">
              <span>Job {runtime.writer.jobId.replace("job-", "#")}</span>
              <span>{runtime.writer.progress ?? 0}%</span>
            </div>
          </>
        ) : (
          <div className="idle-writer">
            <Icon name="activity" />
            <strong>No live transaction</strong>
            <span>The writer lease is available.</span>
          </div>
        )}
      </section>

      <section className="queue-section">
        <div className="section-title">
          <h3>Commit queue</h3>
          <span>{runtime.queue.length}</span>
        </div>
        {runtime.queue.length ? (
          <ol className="queue-list">
            {runtime.queue.map((item, index) => (
              <li key={item.id} className={`queue-item queue-${item.state}`}>
                <span className="queue-index">{String(index + 1).padStart(2, "0")}</span>
                <div>
                  <strong>{item.title}</strong>
                  <span>{sessionTitle(runtime.sessions, item.sessionId)}</span>
                  <small>{item.waitingFor ?? item.resource ?? item.state}</small>
                </div>
              </li>
            ))}
          </ol>
        ) : (
          <p className="quiet-empty">No jobs are waiting.</p>
        )}
      </section>

      <section className="conflicts-section">
        <div className="section-title">
          <h3>Conflicts</h3>
          <span className={runtime.conflicts.length ? "alert-count" : ""}>{runtime.conflicts.length}</span>
        </div>
        {runtime.conflicts.length ? (
          runtime.conflicts.map((conflict) => (
            <ConflictCard conflict={conflict} sessions={runtime.sessions} key={conflict.id} />
          ))
        ) : (
          <p className="quiet-empty">No resource conflicts detected.</p>
        )}
      </section>

      <div className="global-actions">
        <button
          type="button"
          className={`secondary-button ${runtime.paused ? "resume" : ""}`}
          onClick={() => onPauseRuntime(!runtime.paused)}
          disabled={busyActions.has("pause-runtime")}
        >
          <Icon name={runtime.paused ? "play" : "pause"} />
          {runtime.paused ? "Resume all" : "Pause all"}
        </button>
        <button
          type="button"
          className="danger-button"
          onClick={onStop}
          disabled={!runtime.writer || busyActions.has("stop-current")}
        >
          <Icon name="stop" />
          Stop current
        </button>
      </div>
    </aside>
  );
}
