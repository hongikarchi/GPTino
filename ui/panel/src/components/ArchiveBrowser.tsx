import { useEffect, useState } from "react";
import type { ArchiveMessage, ArchiveProject } from "../types";
import { Icon } from "./Icons";

interface ArchiveBrowserProps {
  onClose(): void;
  listArchive(): Promise<ArchiveProject[]>;
  readMessages(fingerprint: string, sessionId: string, limit?: number): Promise<ArchiveMessage[]>;
  /** Fork the selected archived session into the current project. Resolves true on success. */
  importSession(fingerprint: string, sessionId: string): Promise<boolean>;
  /** Restore a soft-deleted session of the CURRENT project (foreign projects are read-only). */
  onRestore(sessionId: string): Promise<boolean | void> | void;
  /** Permanently delete a session of the CURRENT project. */
  onPurge(sessionId: string): Promise<boolean | void> | void;
}

interface SelectedSession {
  fingerprint: string;
  sessionId: string;
  sessionName: string;
  projectName: string;
  /** True when this session belongs to the live/current project (restore & purge apply). */
  current: boolean;
  /** True when this session is soft-deleted. */
  deleted: boolean;
}

const shortFile = (path?: string | null) => (path ? (path.split(/[\\/]/).pop() ?? path) : null);

function relativeTime(iso?: string | null): string {
  if (!iso) return "—";
  const at = Date.parse(iso);
  if (!Number.isFinite(at)) return "—";
  const minutes = Math.round((Date.now() - at) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months}mo ago`;
  return `${Math.round(months / 12)}y ago`;
}

const formatStamp = (iso: string) => {
  const at = new Date(iso);
  return Number.isFinite(at.getTime())
    ? new Intl.DateTimeFormat(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      }).format(at)
    : iso;
};

const roleClass = (role: string) => (role === "user" ? "user" : role === "system" ? "system" : "assistant");
const roleLabel = (role: string) =>
  role === "user" ? "You" : role === "system" ? "System" : role === "assistant" ? "GPTino" : role;

const projectLabel = (project: ArchiveProject) =>
  project.projectName ?? shortFile(project.rhinoFile) ?? project.fingerprint;

export function ArchiveBrowser({ onClose, listArchive, readMessages, importSession, onRestore, onPurge }: ArchiveBrowserProps) {
  const [projects, setProjects] = useState<ArchiveProject[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [listAttempt, setListAttempt] = useState(0);
  const [openFingerprint, setOpenFingerprint] = useState<string | null>(null);
  const [selected, setSelected] = useState<SelectedSession | null>(null);
  const [transcript, setTranscript] = useState<ArchiveMessage[] | null>(null);
  const [transcriptError, setTranscriptError] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [managing, setManaging] = useState(false);

  const handleImport = async () => {
    if (!selected || importing) return;
    setImporting(true);
    setImportError(null);
    const ok = await importSession(selected.fingerprint, selected.sessionId);
    if (ok) {
      onClose();
      return;
    }
    setImporting(false);
    setImportError("Could not import this session into the current project.");
  };

  // Restore / permanently-delete a soft-deleted session of the CURRENT project. Both re-read the
  // archive so the row's state (or absence) reflects the change, and clear the selection.
  const handleRestore = async () => {
    if (!selected || managing) return;
    setManaging(true);
    try {
      await onRestore(selected.sessionId);
      setSelected(null);
      setListAttempt((attempt) => attempt + 1);
    } finally {
      setManaging(false);
    }
  };

  const handlePurge = async () => {
    if (!selected || managing) return;
    if (!window.confirm(`Permanently delete "${selected.sessionName}"? This cannot be undone.`)) return;
    setManaging(true);
    try {
      await onPurge(selected.sessionId);
      setSelected(null);
      setListAttempt((attempt) => attempt + 1);
    } finally {
      setManaging(false);
    }
  };

  useEffect(() => {
    const handleKey = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [onClose]);

  useEffect(() => {
    let disposed = false;
    setProjects(null);
    setListError(null);
    listArchive()
      .then((next) => {
        if (disposed) return;
        setProjects(next);
        const first = next.find((project) => project.available);
        if (first) setOpenFingerprint(first.fingerprint);
      })
      .catch((error: unknown) => {
        if (!disposed) setListError(error instanceof Error ? error.message : "Could not load the archive");
      });
    return () => {
      disposed = true;
    };
  }, [listArchive, listAttempt]);

  useEffect(() => {
    if (!selected) return;
    let disposed = false;
    setTranscript(null);
    setTranscriptError(null);
    setImportError(null);
    setImporting(false);
    readMessages(selected.fingerprint, selected.sessionId)
      .then((messages) => {
        if (!disposed) setTranscript(messages);
      })
      .catch((error: unknown) => {
        if (!disposed) setTranscriptError(error instanceof Error ? error.message : "Could not load the transcript");
      });
    return () => {
      disposed = true;
    };
  }, [readMessages, selected]);

  return (
    <div className="archive-overlay" role="dialog" aria-modal="true" aria-label="Past sessions archive">
      <header className="archive-header">
        <div className="archive-title">
          <Icon name="history" />
          <div>
            <h2>Past sessions</h2>
            <span>Read-only archive of every GPTino project on this machine</span>
          </div>
        </div>
        <button type="button" className="secondary-button" onClick={onClose} title="Close (Esc)">
          Close
        </button>
      </header>

      <div className="archive-body">
        <aside className="archive-list" aria-label="Archived projects">
          {projects === null && listError === null ? <p className="archive-note">Loading the archive…</p> : null}
          {listError !== null ? (
            <div className="archive-error" role="alert">
              <span>{listError}</span>
              <button type="button" onClick={() => setListAttempt((attempt) => attempt + 1)}>
                Retry
              </button>
            </div>
          ) : null}
          {projects !== null && projects.length === 0 ? (
            <p className="archive-note">No GPTino project data was found on this machine.</p>
          ) : null}
          {(projects ?? []).map((project) => {
            const open = openFingerprint === project.fingerprint;
            return (
              <div
                key={project.fingerprint}
                className={`archive-project ${project.available ? "" : "unavailable"} ${open ? "open" : ""}`}
              >
                <button
                  type="button"
                  className="archive-project-head"
                  aria-expanded={open}
                  onClick={() => setOpenFingerprint(open ? null : project.fingerprint)}
                  title={project.fingerprint}
                >
                  <span className="archive-project-name">
                    <strong>{projectLabel(project)}</strong>
                    {project.current ? <span className="archive-badge current">current</span> : null}
                    {!project.available ? <span className="archive-badge">unavailable</span> : null}
                  </span>
                  <span className="archive-project-files">
                    R <b>{shortFile(project.rhinoFile) ?? "—"}</b> · GH <b>{shortFile(project.grasshopperFile) ?? "—"}</b>
                  </span>
                  <span className="archive-project-meta">
                    <span>{relativeTime(project.lastActivityAt)}</span>
                    <span>
                      {project.sessionCount} session{project.sessionCount === 1 ? "" : "s"}
                    </span>
                  </span>
                </button>
                {open ? (
                  <div className="archive-sessions">
                    {!project.available ? (
                      <p className="archive-note">
                        This project&apos;s data could not be read. It may be open in another Rhino instance or damaged.
                      </p>
                    ) : project.sessions.length === 0 ? (
                      <p className="archive-note">No sessions were recorded for this project.</p>
                    ) : (
                      project.sessions.map((session) => (
                        <button
                          type="button"
                          key={session.id}
                          className={`archive-session ${session.deleted ? "deleted" : ""} ${
                            selected?.fingerprint === project.fingerprint && selected.sessionId === session.id
                              ? "selected"
                              : ""
                          }`}
                          onClick={() =>
                            setSelected({
                              fingerprint: project.fingerprint,
                              sessionId: session.id,
                              sessionName: session.name,
                              projectName: projectLabel(project),
                              current: project.current,
                              deleted: session.deleted,
                            })
                          }
                        >
                          <span className="archive-session-name">
                            {session.deleted ? (
                              <span className="archive-session-x" title="Deleted" aria-label="Deleted">
                                ✕
                              </span>
                            ) : null}
                            {session.name}
                          </span>
                          <span className="archive-session-meta">
                            {session.messageCount} msg · {relativeTime(session.updatedAt)}
                          </span>
                        </button>
                      ))
                    )}
                  </div>
                ) : null}
              </div>
            );
          })}
        </aside>

        <section className="archive-transcript" aria-label="Archived transcript">
          {selected === null ? (
            <div className="archive-placeholder">
              <Icon name="history" />
              <strong>Select a session</strong>
              <span>Pick a project on the left, then a session, to read what it did.</span>
            </div>
          ) : (
            <>
              <header className="archive-transcript-header">
                <div className="archive-transcript-title">
                  <strong>{selected.sessionName}</strong>
                  <span>
                    {selected.projectName}
                    {selected.current ? (selected.deleted ? " · deleted" : " · current") : " · read-only"}
                  </span>
                </div>
                {selected.current ? (
                  selected.deleted ? (
                    // A deleted session of THIS project: restore it back to the live rail, or remove it for good.
                    <div className="archive-transcript-actions">
                      <button
                        type="button"
                        className="secondary-button"
                        onClick={() => void handleRestore()}
                        disabled={managing}
                        title="Restore this session to the active list"
                      >
                        {managing ? "Working…" : "Restore"}
                      </button>
                      <button
                        type="button"
                        className="danger-button"
                        onClick={() => void handlePurge()}
                        disabled={managing}
                        title="Permanently delete this session and its transcript"
                      >
                        Delete forever
                      </button>
                    </div>
                  ) : (
                    // A live session already in this project — nothing to import or purge here.
                    <span className="archive-transcript-note">Live in this project</span>
                  )
                ) : (
                  <button
                    type="button"
                    className="archive-import-button"
                    onClick={() => void handleImport()}
                    disabled={importing}
                    title="Create a new session in the current project seeded with this conversation. Its component ids and geometry are stale and will be re-discovered before any change."
                  >
                    <Icon name="history" />
                    {importing ? "Importing…" : "Import into current project"}
                  </button>
                )}
              </header>
              {importError !== null ? (
                <div className="archive-error" role="alert">
                  <span>{importError}</span>
                </div>
              ) : null}
              {transcriptError !== null ? (
                <div className="archive-error" role="alert">
                  <span>{transcriptError}</span>
                  <button type="button" onClick={() => setSelected({ ...selected })}>
                    Retry
                  </button>
                </div>
              ) : transcript === null ? (
                <p className="archive-note">Loading the transcript…</p>
              ) : transcript.length === 0 ? (
                <p className="archive-note">This session has no recorded messages.</p>
              ) : (
                <div className="chat-stream archive-stream">
                  {transcript.map((message) => (
                    <article className={`message message-${roleClass(message.role)}`} key={message.id}>
                      <div className="message-author" title={message.phase ?? undefined}>
                        <span>{roleLabel(message.role)}</span>
                        <time dateTime={message.createdAt}>{formatStamp(message.createdAt)}</time>
                      </div>
                      <p>{message.content}</p>
                    </article>
                  ))}
                </div>
              )}
            </>
          )}
        </section>
      </div>
    </div>
  );
}
