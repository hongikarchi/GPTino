import { useEffect, useState } from "react";
import type { ArchiveMessage, ArchiveProject } from "../types";
import { Icon } from "./Icons";

interface ArchiveBrowserProps {
  onClose(): void;
  listArchive(): Promise<ArchiveProject[]>;
  readMessages(fingerprint: string, sessionId: string, limit?: number): Promise<ArchiveMessage[]>;
}

interface SelectedSession {
  fingerprint: string;
  sessionId: string;
  sessionName: string;
  projectName: string;
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

export function ArchiveBrowser({ onClose, listArchive, readMessages }: ArchiveBrowserProps) {
  const [projects, setProjects] = useState<ArchiveProject[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [listAttempt, setListAttempt] = useState(0);
  const [openFingerprint, setOpenFingerprint] = useState<string | null>(null);
  const [selected, setSelected] = useState<SelectedSession | null>(null);
  const [transcript, setTranscript] = useState<ArchiveMessage[] | null>(null);
  const [transcriptError, setTranscriptError] = useState<string | null>(null);

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
                          className={`archive-session ${
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
                            })
                          }
                        >
                          <span className="archive-session-name">{session.name}</span>
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
                <strong>{selected.sessionName}</strong>
                <span>{selected.projectName} · read-only</span>
              </header>
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
