import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import type { ChatMessage, GptinoSession, ModelInfo, ModelProfile, SessionActivity, SessionMode } from "../types";
import { Icon } from "./Icons";
import { StatusBadge } from "./StatusBadge";

interface ChatPaneProps {
  session: GptinoSession | undefined;
  models: ModelInfo[];
  busyActions: Set<string>;
  onMode(mode: SessionMode): void;
  onModel(profile: ModelProfile): void;
  onPinModel(model: string | null): void;
  onSend(content: string): Promise<void> | void;
}

type StreamItem =
  | { type: "message"; at: number; message: ChatMessage }
  | { type: "activity"; at: number; activity: SessionActivity };

const profiles: { value: ModelProfile; label: string; description: string }[] = [
  { value: "auto", label: "Auto", description: "Route by task risk" },
  { value: "fast", label: "Fast", description: "Simple, typed operations" },
  { value: "standard", label: "Standard", description: "General modeling" },
  { value: "deep", label: "Deep", description: "Complex and recovery work" },
];

const formatTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));

export function ChatPane({ session, models, busyActions, onMode, onModel, onPinModel, onSend }: ChatPaneProps) {
  const [draft, setDraft] = useState("");
  const streamRef = useRef<HTMLDivElement>(null);

  const stream = useMemo<StreamItem[]>(() => {
    if (!session) return [];
    const items: StreamItem[] = session.messages.map((message) => ({
      type: "message",
      at: Date.parse(message.createdAt) || 0,
      message,
    }));
    for (const activity of session.activity ?? []) {
      items.push({ type: "activity", at: Date.parse(activity.at) || 0, activity });
    }
    return items.sort((a, b) => a.at - b.at);
  }, [session]);

  useEffect(() => {
    streamRef.current?.scrollTo({ top: streamRef.current.scrollHeight, behavior: "smooth" });
  }, [session?.id, stream.length]);

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
      </header>

      <div className="chat-stream" ref={streamRef} aria-live="polite">
        {stream.map((item) =>
          item.type === "message" ? (
            <article
              className={`message message-${item.message.role} ${item.message.pending ? "pending" : ""}`}
              key={`m-${item.message.id}`}
            >
              <div className="message-author">
                <span>
                  {item.message.role === "assistant" ? "GPTino" : item.message.role === "system" ? "System" : "You"}
                </span>
                <time dateTime={item.message.createdAt}>{formatTime(item.message.createdAt)}</time>
              </div>
              <p>{item.message.content}</p>
              {item.message.pending ? <span className="pending-label">Sending…</span> : null}
            </article>
          ) : (
            <div
              className={`activity-row ${item.activity.ok ? "" : "failed"}`}
              key={`a-${item.at}-${item.activity.kind}-${item.activity.summary}`}
              title={`${item.activity.kind}${item.activity.durationMs > 0 ? ` · ${item.activity.durationMs}ms` : ""}`}
            >
              <span className="activity-dot" />
              <span className="activity-text">{item.activity.summary}</span>
              <time dateTime={item.activity.at}>{formatTime(item.activity.at)}</time>
            </div>
          ),
        )}
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
          {models.length > 0 ? (
            <div className="quality-control">
              <label htmlFor="model-pin">Model</label>
              <select
                id="model-pin"
                value={session.pinnedModel ?? ""}
                onChange={(event) => onPinModel(event.target.value || null)}
                disabled={busyActions.has(`model:${session.id}`)}
                title="Pin a Codex model for this session. Quality still sets the capability floor and reasoning effort."
              >
                <option value="">Auto (routed)</option>
                {models.map((model) => (
                  <option value={model.model} key={model.id} title={model.description}>
                    {model.displayName || model.model}
                  </option>
                ))}
              </select>
            </div>
          ) : null}
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
