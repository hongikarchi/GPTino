import { useCallback, useEffect, useState } from "react";
import type { DeletedSession } from "../types";

interface DeletedSessionsProps {
  onClose(): void;
  list(): Promise<DeletedSession[]>;
  onRestore(sessionId: string): Promise<boolean | void> | void;
  onPurge(sessionId: string): Promise<boolean | void> | void;
}

/** Trash view for soft-deleted sessions: restore them, or delete them permanently. */
export function DeletedSessions({ onClose, list, onRestore, onPurge }: DeletedSessionsProps) {
  const [sessions, setSessions] = useState<DeletedSession[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [busyId, setBusyId] = useState<string | null>(null);

  const refresh = useCallback(() => setAttempt((n) => n + 1), []);

  useEffect(() => {
    let cancelled = false;
    setSessions(null);
    setError(null);
    list()
      .then((rows) => {
        if (!cancelled) setSessions(rows);
      })
      .catch((loadError) => {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : "Failed to load deleted sessions");
        }
      });
    return () => {
      cancelled = true;
    };
  }, [list, attempt]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const restore = async (sessionId: string) => {
    setBusyId(sessionId);
    try {
      await onRestore(sessionId);
      refresh();
    } finally {
      setBusyId(null);
    }
  };

  const purge = async (session: DeletedSession) => {
    if (!window.confirm(`Permanently delete "${session.name}"? This cannot be undone.`)) return;
    setBusyId(session.id);
    try {
      await onPurge(session.id);
      refresh();
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="archive-overlay" role="dialog" aria-modal="true" aria-label="Deleted sessions">
      <header className="archive-header">
        <div className="archive-title">
          <h2>Deleted sessions</h2>
        </div>
        <button type="button" className="secondary-button" onClick={onClose} title="Close (Esc)">
          Close
        </button>
      </header>
      <div className="archive-body">
        <div className="archive-list" aria-label="Deleted sessions list">
          {sessions === null && !error ? <p className="archive-note">Loading…</p> : null}
          {error ? (
            <div className="archive-error" role="alert">
              {error}
            </div>
          ) : null}
          {sessions && sessions.length === 0 ? (
            <p className="archive-note">No deleted sessions. Deleting a session moves it here first.</p>
          ) : null}
          {sessions?.map((session) => (
            <div key={session.id} className="deleted-session-row">
              <span className="archive-project-name">{session.name}</span>
              <span className="deleted-session-actions">
                <button
                  type="button"
                  className="secondary-button"
                  disabled={busyId === session.id}
                  onClick={() => void restore(session.id)}
                >
                  Restore
                </button>
                <button
                  type="button"
                  className="danger-button"
                  disabled={busyId === session.id}
                  onClick={() => void purge(session)}
                >
                  Delete forever
                </button>
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
