import { useState, type DragEvent } from "react";
import type { GptinoSession } from "../types";
import { Icon } from "./Icons";
import { StatusBadge } from "./StatusBadge";

interface SessionListProps {
  sessions: GptinoSession[];
  selectedId: string | null;
  busyActions: Set<string>;
  onSelect(id: string): void;
  onCreate(): void;
  onReorder(sourceId: string, targetId: string): void;
  onShift(id: string, direction: -1 | 1): void;
  onPause(id: string, paused: boolean): void;
  onTerminal(id: string): void;
}

const modelLabels = {
  auto: "Auto",
  fast: "Fast",
  standard: "Std",
  deep: "Deep",
};

export function SessionList({
  sessions,
  selectedId,
  busyActions,
  onSelect,
  onCreate,
  onReorder,
  onShift,
  onPause,
  onTerminal,
}: SessionListProps) {
  const [draggingId, setDraggingId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);

  const drop = (event: DragEvent, targetId: string) => {
    event.preventDefault();
    const sourceId = draggingId ?? event.dataTransfer.getData("text/plain");
    if (sourceId) onReorder(sourceId, targetId);
    setDraggingId(null);
    setOverId(null);
  };

  return (
    <section className="sessions-pane" aria-label="Ordered sessions">
      <div className="pane-heading">
        <div>
          <span className="eyebrow">Priority order</span>
          <h2>Sessions</h2>
        </div>
        <div className="session-heading-actions">
          <span className="count-chip">{sessions.length}</span>
          <button
            type="button"
            className="new-session-button"
            onClick={onCreate}
            disabled={busyActions.has("create-session")}
          >
            <span aria-hidden="true">+</span>
            New
          </button>
        </div>
      </div>
      <p className="pane-note">Drag sessions to choose which ready task runs first.</p>

      <ol className="session-list">
        {sessions.map((session, index) => {
          const selected = session.id === selectedId;
          return (
            <li
              className={`session-card ${selected ? "selected" : ""} ${draggingId === session.id ? "dragging" : ""} ${overId === session.id ? "drag-over" : ""}`}
              key={session.id}
              draggable
              onDragStart={(event) => {
                setDraggingId(session.id);
                event.dataTransfer.effectAllowed = "move";
                event.dataTransfer.setData("text/plain", session.id);
              }}
              onDragEnd={() => {
                setDraggingId(null);
                setOverId(null);
              }}
              onDragEnter={() => setOverId(session.id)}
              onDragOver={(event) => {
                event.preventDefault();
                event.dataTransfer.dropEffect = "move";
              }}
              onDrop={(event) => drop(event, session.id)}
            >
              <div className="session-rank" aria-label={`Priority ${index + 1}`}>
                {String(index + 1).padStart(2, "0")}
              </div>
              <button
                className="drag-handle"
                type="button"
                aria-label={`Drag ${session.title} to reorder`}
                title="Drag to reorder"
                onClick={() => onSelect(session.id)}
              >
                <Icon name="drag" />
              </button>
              <button className="session-main" type="button" onClick={() => onSelect(session.id)}>
                <span className="session-title-row">
                  <strong>{session.title}</strong>
                  {session.unread ? <span className="unread-dot" aria-label={`${session.unread} unread`} /> : null}
                </span>
                <span className="session-summary">{session.summary ?? "No active task"}</span>
                <span className="session-meta">
                  <StatusBadge status={session.status} />
                  <span className={`model-badge model-${session.modelProfile}`}>{modelLabels[session.modelProfile]}</span>
                  <span className={`mode-label mode-${session.mode}`}>{session.mode === "auto" ? "Auto" : "Plan"}</span>
                </span>
              </button>
              <div className="session-actions">
                <div className="reorder-buttons" aria-label="Keyboard reorder controls">
                  <button
                    type="button"
                    className="icon-button micro"
                    disabled={index === 0 || busyActions.has("reorder")}
                    onClick={() => onShift(session.id, -1)}
                    aria-label={`Move ${session.title} up`}
                  >
                    <Icon name="arrowUp" />
                  </button>
                  <button
                    type="button"
                    className="icon-button micro"
                    disabled={index === sessions.length - 1 || busyActions.has("reorder")}
                    onClick={() => onShift(session.id, 1)}
                    aria-label={`Move ${session.title} down`}
                  >
                    <Icon name="arrowDown" />
                  </button>
                </div>
                <button
                  type="button"
                  className={`icon-button ${session.terminalOpen ? "active" : ""}`}
                  disabled={busyActions.has(`terminal:${session.id}`)}
                  onClick={() => onTerminal(session.id)}
                  aria-label={`Open terminal for ${session.title}`}
                  title="Open attached terminal"
                >
                  <Icon name="terminal" />
                </button>
                <button
                  type="button"
                  className={`icon-button ${session.paused ? "warning" : ""}`}
                  disabled={busyActions.has(`pause:${session.id}`)}
                  onClick={() => onPause(session.id, !session.paused)}
                  aria-label={`${session.paused ? "Resume" : "Pause"} ${session.title}`}
                  title={session.paused ? "Resume session" : "Pause session"}
                >
                  <Icon name={session.paused ? "play" : "pause"} />
                </button>
              </div>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
