import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import type { GptinoSession, ModelProfile, SessionMode } from "../types";
import { Icon } from "./Icons";
import { StatusBadge } from "./StatusBadge";

interface ChatPaneProps {
  session: GptinoSession | undefined;
  busyActions: Set<string>;
  onMode(mode: SessionMode): void;
  onModel(profile: ModelProfile): void;
  onSend(content: string): Promise<void> | void;
  onTerminal(): void;
}

const profiles: { value: ModelProfile; label: string; description: string }[] = [
  { value: "auto", label: "Auto", description: "Route by task risk" },
  { value: "fast", label: "Fast", description: "Simple, typed operations" },
  { value: "standard", label: "Standard", description: "General modeling" },
  { value: "deep", label: "Deep", description: "Complex and recovery work" },
];

const formatTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));

export function ChatPane({ session, busyActions, onMode, onModel, onSend, onTerminal }: ChatPaneProps) {
  const [draft, setDraft] = useState("");
  const streamRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    streamRef.current?.scrollTo({ top: streamRef.current.scrollHeight, behavior: "smooth" });
  }, [session?.id, session?.messages.length]);

  if (!session) {
    return (
      <section className="chat-pane empty-state">
        <div className="empty-mark">G</div>
        <h2>Select a session</h2>
        <p>Choose a workstream to view its context and send instructions.</p>
      </section>
    );
  }

  const sending = busyActions.has(`message:${session.id}`);
  const submit = async () => {
    const content = draft.trim();
    if (!content || sending) return;
    setDraft("");
    await onSend(content);
  };

  const handleComposerKey = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void submit();
      return;
    }
    if (event.key === "Tab" && event.shiftKey) {
      event.preventDefault();
      onMode(session.mode === "auto" ? "plan" : "auto");
    }
  };

  return (
    <section className="chat-pane" aria-label={`${session.title} chat`}>
      <header className="chat-header">
        <div className="chat-title-block">
          <span className="eyebrow">Active session</span>
          <div className="chat-title-row">
            <h2>{session.title}</h2>
            <StatusBadge status={session.status} />
          </div>
          <p>{session.summary}</p>
        </div>
        <button type="button" className="terminal-button" onClick={onTerminal}>
          <Icon name="terminal" />
          <span>Terminal</span>
          {session.terminalOpen ? <span className="terminal-live" /> : null}
        </button>
      </header>

      <div className="chat-stream" ref={streamRef} aria-live="polite">
        {session.messages.map((message) => (
          <article className={`message message-${message.role} ${message.pending ? "pending" : ""}`} key={message.id}>
            <div className="message-author">
              <span>{message.role === "assistant" ? "GPTino" : message.role === "system" ? "System" : "You"}</span>
              <time dateTime={message.createdAt}>{formatTime(message.createdAt)}</time>
            </div>
            <p>{message.content}</p>
            {message.pending ? <span className="pending-label">Sending…</span> : null}
          </article>
        ))}
        {session.status === "drafting" || session.status === "working" ? (
          <div className="thinking-row" aria-label="GPTino is working">
            <span />
            <span />
            <span />
            <em>{session.status === "drafting" ? "Drafting a ChangeSet" : "Working"}</em>
          </div>
        ) : null}
      </div>

      <div className="composer-wrap">
        <div className="control-strip">
          <div className="segmented" aria-label="Session execution mode">
            {(["plan", "auto"] as SessionMode[]).map((mode) => (
              <button
                type="button"
                className={session.mode === mode ? "active" : ""}
                key={mode}
                onClick={() => onMode(mode)}
                disabled={busyActions.has(`mode:${session.id}`)}
              >
                {mode === "plan" ? "Plan" : "Auto"}
              </button>
            ))}
          </div>
          <div className="quality-control">
            <label htmlFor="model-profile">Quality</label>
            <select
              id="model-profile"
              value={session.modelProfile}
              onChange={(event) => onModel(event.target.value as ModelProfile)}
              disabled={busyActions.has(`model:${session.id}`)}
            >
              {profiles.map((profile) => (
                <option value={profile.value} key={profile.value} title={profile.description}>
                  {profile.label}
                </option>
              ))}
            </select>
          </div>
          <span
            className="effective-model"
            title={session.routingError ?? session.routingReason ?? "Effective model and reasoning"}
          >
            {session.effectiveModel ?? "Routing pending"}
            {session.reasoning ? ` / ${session.reasoning}` : ""}
            {session.effectiveProfile ? ` / ${session.effectiveProfile}` : ""}
          </span>
        </div>

        <div className="composer">
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={handleComposerKey}
            placeholder={session.paused ? "Session is paused — resume it to continue" : "Describe the next modeling change…"}
            aria-label="Message GPTino"
            rows={3}
            disabled={session.paused}
          />
          <button
            type="button"
            className="send-button"
            onClick={() => void submit()}
            disabled={!draft.trim() || sending || session.paused}
            aria-label="Send instruction"
          >
            <Icon name="send" />
          </button>
        </div>
        <div className="composer-hint">
          <span>Ctrl ↵ to send</span>
          <span>Shift ⇥ toggles Plan / Auto</span>
        </div>
      </div>
    </section>
  );
}
