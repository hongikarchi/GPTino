import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type ClipboardEvent,
  type DragEvent,
  type KeyboardEvent,
} from "react";
import type {
  ChatMessage,
  CodexLimits,
  GptinoSession,
  GrasshopperDocInfo,
  MessageAttachment,
  ModelInfo,
  ModelProfile,
  RuntimeConflict,
  SessionActivity,
  SessionMode,
  SessionUsage,
} from "../types";
import { Icon } from "./Icons";
import { StatusBadge } from "./StatusBadge";

interface ChatPaneProps {
  session: GptinoSession | undefined;
  conflicts: RuntimeConflict[];
  models: ModelInfo[];
  /** Account-scoped codex rate limits for the status line; null before the first turn reports them. */
  limits?: CodexLimits | null;
  /** Registered GH docs; the target selector renders when more than one exists OR the session carries a (possibly stale) binding. */
  grasshopperDocs?: GrasshopperDocInfo[] | null;
  busyActions: Set<string>;
  onMode(mode: SessionMode): void;
  onModel(profile: ModelProfile): void;
  onPinModel(model: string | null): void;
  /** Toggle the session's native Codex goal (objective + budget tracking) on/off. */
  onGoal(enabled: boolean): void;
  /** Bind the session's writes to a GH doc (docKey) or unbind with null. */
  onTarget(grasshopperDoc: string | null): void;
  /** Resolves false when the send failed (the composer restores its draft). */
  onSend(content: string, attachments?: MessageAttachment[]): Promise<boolean | void> | void;
  /** Resume a paused session (the composer is disabled while paused). */
  onResume(): void;
  /** Soft-delete this session (hidden from the list, recoverable from the trash). */
  onDelete(): void;
  /** Stop the current turn and retract the last user message; resolves its text (or null) to edit. */
  onStopEdit(): Promise<string | null>;
}

/** One staged composer attachment; the bytes stay in the File until send encodes them. */
interface PendingAttachment {
  id: string;
  file: File;
  fileName: string;
  mediaType: string;
  size: number;
}

// Mirrors the AgentHost AttachmentStore limits so violations surface before the round-trip.
const MAX_ATTACHMENTS = 4;
const MAX_TOTAL_ATTACHMENT_BYTES = 8 * 1024 * 1024;
const ALLOWED_MEDIA_TYPES = new Set([
  "image/png",
  "image/jpeg",
  "image/webp",
  "image/gif",
  "text/plain",
  "text/markdown",
  "application/json",
  "text/csv",
  "application/pdf",
]);
// Windows often reports no MIME type for text-ish extensions; map them before rejecting.
const EXTENSION_MEDIA_TYPES: Record<string, string> = {
  png: "image/png",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  webp: "image/webp",
  gif: "image/gif",
  txt: "text/plain",
  md: "text/markdown",
  markdown: "text/markdown",
  json: "application/json",
  csv: "text/csv",
  pdf: "application/pdf",
};
const ATTACHMENT_ACCEPT = [...ALLOWED_MEDIA_TYPES, ".md", ".markdown", ".txt", ".json", ".csv"].join(",");

const resolveMediaType = (file: File): string | undefined => {
  if (ALLOWED_MEDIA_TYPES.has(file.type)) return file.type;
  const extension = file.name.split(".").pop()?.toLowerCase() ?? "";
  return EXTENSION_MEDIA_TYPES[extension];
};

const formatBytes = (bytes: number) =>
  bytes >= 1024 * 1024
    ? `${(bytes / (1024 * 1024)).toFixed(1)} MB`
    : bytes >= 1024
      ? `${Math.round(bytes / 1024)} KB`
      : `${bytes} B`;

const encodeAttachment = (item: PendingAttachment): Promise<MessageAttachment> =>
  new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error(`Could not read "${item.fileName}".`));
    reader.onload = () => {
      const result = String(reader.result ?? "");
      const comma = result.indexOf(",");
      resolve({
        fileName: item.fileName,
        mediaType: item.mediaType,
        dataBase64: comma >= 0 ? result.slice(comma + 1) : result,
      });
    };
    reader.readAsDataURL(item.file);
  });

type StreamItem =
  | { type: "message"; at: number; message: ChatMessage }
  | { type: "activity"; at: number; activity: SessionActivity };

// Reasoning-effort levels, ascending. The slider offers whichever of these the chosen model advertises.
const EFFORT_ORDER: ModelProfile[] = ["low", "medium", "high", "xhigh", "max", "ultra"];
const EFFORT_LABELS: Record<ModelProfile, string> = {
  low: "Low",
  medium: "Medium",
  high: "High",
  xhigh: "Extra High",
  max: "Max",
  ultra: "Ultra",
};
const effortRank = (value: string): number => {
  const index = EFFORT_ORDER.indexOf(value as ModelProfile);
  return index < 0 ? EFFORT_ORDER.length : index;
};

const formatTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(new Date(value));

const compactTokens = (value: number) =>
  value >= 1_000_000 ? `${(value / 1_000_000).toFixed(1)}M` : value >= 1_000 ? `${Math.round(value / 1_000)}k` : `${value}`;

// Window labels sort shortest first: "45m" < "5h" < "weekly" < "3d" < anything unparsed.
const windowRank = (label: string): number => {
  const match = /^(\d+(?:\.\d+)?)([mhd])$/.exec(label);
  if (match) {
    const value = Number(match[1]);
    return match[2] === "m" ? value : match[2] === "h" ? value * 60 : value * 1_440;
  }
  return label === "weekly" ? 10_080 : Number.MAX_SAFE_INTEGER;
};

const usageTone = (usedPercent: number) =>
  usedPercent >= 90 ? "critical" : usedPercent >= 70 ? "warn" : "";

// One fuel-gauge segment of the status line: the filled part is what REMAINS.
function UsageMeter({ label, usedPercent, title }: { label: string; usedPercent: number; title: string }) {
  const left = Math.max(0, Math.min(100, Math.round(100 - usedPercent)));
  return (
    <span className={`usage-meter ${usageTone(usedPercent)}`} title={title}>
      <b>{label}</b>
      <span className="meter-track">
        <span className="meter-fill" style={{ width: `${left}%` }} />
      </span>
      {left}%
    </span>
  );
}

// codex-CLI-style status line under the composer: the selected session's
// context left plus the account's provider windows (5h / weekly). Every figure
// shows what REMAINS; used% and reset times live in the tooltips. Renders
// nothing until the first turn reports usage. Null guards are null-inclusive
// (`!= null`) because the server serializes absent values as explicit nulls.
function UsageStatusLine({ usage, limits }: { usage?: SessionUsage; limits?: CodexLimits | null }) {
  const hasContext = usage?.contextWindow != null && usage.contextWindow > 0 && usage.contextUsedTokens != null;
  const contextUsedPercent = hasContext
    ? Math.min(100, (usage!.contextUsedTokens! / usage!.contextWindow!) * 100)
    : undefined;
  const windows = [...(limits?.windows ?? [])].sort((a, b) => windowRank(a.label) - windowRank(b.label));
  if (contextUsedPercent === undefined && windows.length === 0) return null;

  return (
    <span className="usage-line" aria-label="Remaining codex tokens">
      {contextUsedPercent !== undefined ? (
        <UsageMeter
          label="ctx"
          usedPercent={contextUsedPercent}
          title={[
            `Context: ${compactTokens(usage!.contextUsedTokens!)} of ${compactTokens(usage!.contextWindow!)} tokens used (${Math.round(contextUsedPercent)}%)`,
            usage!.totalTokens != null ? `Session total: ${compactTokens(usage!.totalTokens)} tokens` : null,
          ]
            .filter(Boolean)
            .join("\n")}
        />
      ) : null}
      {windows.map((window) => {
        // A reset time in the past means the provider window rolled over since
        // the last turn — report it as full instead of a stale used%.
        const reset = window.resetsAt ? Date.parse(window.resetsAt) : Number.NaN;
        const rolledOver = !Number.isNaN(reset) && reset < Date.now();
        return (
          <UsageMeter
            key={window.label}
            label={window.label}
            usedPercent={rolledOver ? 0 : window.usedPercent}
            title={[
              rolledOver
                ? `${window.label} window reset ${formatTime(window.resetsAt!)} — full again.`
                : `${window.label} window: ${Math.round(window.usedPercent)}% used${window.resetsAt ? ` · resets ${formatTime(window.resetsAt)}` : ""}`,
              limits?.updatedAt ? `As of the last turn (${formatTime(limits.updatedAt)})` : null,
            ]
              .filter(Boolean)
              .join("\n")}
          />
        );
      })}
    </span>
  );
}

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

export function ChatPane({ session, conflicts, models, limits, grasshopperDocs, busyActions, onMode, onModel, onPinModel, onGoal, onTarget, onSend, onResume, onDelete, onStopEdit }: ChatPaneProps) {
  const [draft, setDraft] = useState("");
  const [pending, setPending] = useState<PendingAttachment[]>([]);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const [effortOpen, setEffortOpen] = useState(false);

  // The slider offers the reasoning-effort levels advertised by the chosen model (pinned, else the
  // catalog default), sorted ascending. Falls back to the full ladder before the catalog loads.
  const catalogModel =
    (session && models.find((model) => model.model === session.pinnedModel)) ??
    models.find((model) => model.isDefault) ??
    models[0];
  const effortLevels: ModelProfile[] = ((catalogModel?.reasoningEfforts?.length
    ? catalogModel.reasoningEfforts
    : EFFORT_ORDER) as string[])
    .filter((level): level is ModelProfile => EFFORT_ORDER.includes(level as ModelProfile))
    .sort((a, b) => effortRank(a) - effortRank(b));
  const streamRef = useRef<HTMLDivElement>(null);
  const effortRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const pasteCounter = useRef(0);
  const submitGate = useRef(false);

  const sessionConflicts = useMemo(
    () => (session ? conflicts.filter((conflict) => conflict.sessionIds.includes(session.id)) : []),
    [conflicts, session],
  );

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

  // Switching sessions jumps straight to the newest message (an animated scroll
  // through the whole backlog is disorienting); new items in the same session
  // still glide down smoothly. The ref bookkeeping runs before the element
  // guard so empty-state renders (no stream div) still record the id — without
  // that, deleting and restoring the same session would smooth-scroll.
  const prevSessionId = useRef<string | undefined>(undefined);
  useEffect(() => {
    const switched = prevSessionId.current !== session?.id;
    prevSessionId.current = session?.id;
    const el = streamRef.current;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior: switched ? "auto" : "smooth" });
  }, [session?.id, stream.length]);

  // Like a native <select>, the effort popover cannot outlive its session: it
  // closes whenever the selected session changes or disappears.
  useEffect(() => {
    setEffortOpen(false);
  }, [session?.id]);

  // The effort popover closes like the model dropdown: Esc or a press anywhere
  // outside the control dismisses it. Capture phase, because canvas nodes call
  // stopPropagation() on pointerdown — a bubble listener would never see those.
  useEffect(() => {
    if (!effortOpen) return;
    const handlePointerDown = (event: PointerEvent) => {
      const anchor = effortRef.current;
      // An unmounted anchor (empty state) can never contain the press — close.
      if (!anchor || (event.target instanceof Node && !anchor.contains(event.target))) {
        setEffortOpen(false);
      }
    };
    const handleKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") setEffortOpen(false);
    };
    document.addEventListener("pointerdown", handlePointerDown, true);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown, true);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [effortOpen]);

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

  const addFiles = (incoming: File[]) => {
    if (incoming.length === 0) return;
    const next = [...pending];
    let total = next.reduce((sum, item) => sum + item.size, 0);
    let error: string | null = null;
    for (const file of incoming) {
      if (next.length >= MAX_ATTACHMENTS) {
        error = `A message can carry at most ${MAX_ATTACHMENTS} attachments.`;
        break;
      }
      const mediaType = resolveMediaType(file);
      if (!mediaType) {
        error = `"${file.name}" is not a supported type (images, text, Markdown, JSON, CSV, PDF).`;
        continue;
      }
      if (file.size === 0) {
        error = `"${file.name}" is empty.`;
        continue;
      }
      if (total + file.size > MAX_TOTAL_ATTACHMENT_BYTES) {
        error = "Attachments exceed the 8 MiB limit per message.";
        continue;
      }
      total += file.size;
      next.push({ id: crypto.randomUUID(), file, fileName: file.name, mediaType, size: file.size });
    }
    setPending(next);
    setAttachmentError(error);
  };

  const removeAttachment = (id: string) => {
    setPending((current) => current.filter((item) => item.id !== id));
    setAttachmentError(null);
  };

  const handleFileInput = (event: ChangeEvent<HTMLInputElement>) => {
    addFiles(Array.from(event.target.files ?? []));
    event.target.value = "";
  };

  const handlePaste = (event: ClipboardEvent<HTMLTextAreaElement>) => {
    const images = Array.from(event.clipboardData?.items ?? []).filter(
      (item) => item.kind === "file" && item.type.startsWith("image/"),
    );
    if (images.length === 0 || sending) return;
    event.preventDefault();
    const files: File[] = [];
    for (const item of images) {
      const file = item.getAsFile();
      if (!file) continue;
      pasteCounter.current += 1;
      files.push(new File([file], `pasted-${pasteCounter.current}.png`, { type: file.type || "image/png" }));
    }
    addFiles(files);
  };

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    if (sending || session.paused) return;
    event.preventDefault();
    setDragging(true);
  };

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    if (sending || session.paused) return;
    addFiles(Array.from(event.dataTransfer?.files ?? []));
  };

  const submit = async () => {
    const content = draft.trim();
    if ((!content && pending.length === 0) || sending || submitGate.current) return;
    submitGate.current = true;
    try {
      // Snapshot what this submit sends: attachments pasted mid-encode stay
      // staged for the next send, and a failed send restores the draft.
      const toSend = [...pending];
      let attachments: MessageAttachment[] | undefined;
      if (toSend.length > 0) {
        try {
          attachments = await Promise.all(toSend.map(encodeAttachment));
        } catch (encodeError) {
          setAttachmentError(encodeError instanceof Error ? encodeError.message : "Could not read an attachment.");
          return;
        }
      }
      const savedDraft = draft;
      setDraft("");
      setAttachmentError(null);
      const ok = await onSend(content, attachments);
      if (ok === false) {
        setDraft(savedDraft);
        return;
      }
      setPending((current) => current.filter((item) => !toSend.some((sent) => sent.id === item.id)));
    } finally {
      submitGate.current = false;
    }
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
          <div className="chat-title-row">
            <h2>{session.title}</h2>
            <StatusBadge status={session.status} />
          </div>
          <p>{session.summary}</p>
        </div>
        {session.paused ? (
          <button
            type="button"
            className="chat-resume"
            title="Resume this paused session"
            disabled={busyActions.has(`pause:${session.id}`)}
            onClick={onResume}
          >
            Resume
          </button>
        ) : null}
        <button
          type="button"
          className="chat-delete"
          title="Delete session (recoverable from Deleted)"
          disabled={busyActions.has(`delete:${session.id}`)}
          onClick={() => {
            if (window.confirm(`Delete session "${session.title}"? You can restore it from Deleted.`)) {
              onDelete();
            }
          }}
        >
          Delete
        </button>
      </header>

      <div className="chat-stream" ref={streamRef} aria-live="polite">
        {session.status === "blocked" && sessionConflicts.length > 0 ? (
          <div className="blocked-callout" role="alert">
            {sessionConflicts.map((conflict) => (
              <div key={conflict.id}>
                <strong>
                  <Icon name="warning" /> {conflict.title}
                </strong>
                <p>{conflict.detail}</p>
                {conflict.resolution ? (
                  <p className="conflict-solution">
                    <b>Solution</b> — {conflict.resolution}
                  </p>
                ) : null}
              </div>
            ))}
          </div>
        ) : null}
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
              {/* Ellipsized summaries stay recoverable: the text carries its own tooltip. */}
              <span className="activity-text" title={item.activity.summary}>{item.activity.summary}</span>
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
            <button
              type="button"
              className="stop-edit-button"
              title="Stop the current work and pull your message back to edit it"
              onClick={async () => {
                const content = await onStopEdit();
                if (typeof content === "string") {
                  setDraft((current) => (current.trim().length > 0 ? current : content));
                }
              }}
            >
              Stop &amp; edit
            </button>
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
          <div className="quality-control effort-control" ref={effortRef}>
            <button
              type="button"
              className="effort-toggle"
              onClick={() => setEffortOpen((open) => !open)}
              disabled={busyActions.has(`model:${session.id}`)}
              aria-expanded={effortOpen}
              title="Reasoning effort for this session (used directly; clamped to the model's range)."
            >
              <span className="effort-caption">Effort</span>
              <span className="effort-value">{EFFORT_LABELS[session.modelProfile] ?? session.modelProfile}</span>
            </button>
            {effortOpen ? (
              <div className="effort-slider">
                <input
                  type="range"
                  min={0}
                  max={Math.max(0, effortLevels.length - 1)}
                  step={1}
                  value={Math.max(0, effortLevels.indexOf(session.modelProfile))}
                  onChange={(event) => onModel(effortLevels[Number(event.target.value)] ?? session.modelProfile)}
                  disabled={busyActions.has(`model:${session.id}`)}
                  aria-label="Reasoning effort"
                />
                <div className="effort-ticks">
                  {effortLevels.map((level) => (
                    <span key={level} className={level === session.modelProfile ? "active" : ""}>
                      {EFFORT_LABELS[level] ?? level}
                    </span>
                  ))}
                </div>
              </div>
            ) : null}
          </div>
          {models.length > 0 ? (
            <div className="quality-control">
              <label htmlFor="model-pin">Model</label>
              <select
                id="model-pin"
                value={session.pinnedModel ?? ""}
                onChange={(event) => onPinModel(event.target.value || null)}
                disabled={busyActions.has(`model:${session.id}`)}
                title="Pin a Codex model for this session, or Auto to use the catalog default. Effort is set separately."
              >
                <option value="">Auto (default)</option>
                {models.map((model) => (
                  <option value={model.model} key={model.id} title={model.description}>
                    {model.displayName || model.model}
                  </option>
                ))}
              </select>
            </div>
          ) : null}
          <div className="quality-control goal-control">
            <label htmlFor="goal-toggle">Goal</label>
            <button
              type="button"
              id="goal-toggle"
              className={`goal-toggle${session.goalEnabled ? " on" : ""}`}
              role="switch"
              aria-checked={session.goalEnabled ?? false}
              onClick={() => onGoal(!(session.goalEnabled ?? false))}
              disabled={busyActions.has(`goal:${session.id}`)}
              title="Give this session's Codex thread a native goal (objective + progress/budget tracking). GPTino acceptance predicates still decide 'done'."
            >
              {session.goalEnabled ? "On" : "Off"}
            </button>
          </div>
          {(grasshopperDocs && grasshopperDocs.length > 1) || session.boundGrasshopperDocId != null ? (
            // Also rendered when the bound doc is no longer registered (even with 0-1 docs
            // left): the selector is the panel's only unbind path, so hiding it would strand
            // the session on a binding that fails every submit.
            <div className="quality-control">
              <label htmlFor="session-target">Target</label>
              <select
                id="session-target"
                value={session.boundGrasshopperDocId ?? ""}
                onChange={(event) => onTarget(event.target.value || null)}
                disabled={busyActions.has(`target:${session.id}`)}
                title="Bind this session's writes to one Grasshopper document. Unbound sessions must pick a document before submitting changes."
              >
                <option value="">Unbound</option>
                {(grasshopperDocs ?? []).map((doc) => (
                  <option value={doc.id} key={doc.id} title={doc.file}>
                    {shortFile(doc.file)}
                  </option>
                ))}
                {session.boundGrasshopperDocId != null &&
                !(grasshopperDocs ?? []).some((doc) => doc.id === session.boundGrasshopperDocId) ? (
                  // A disabled option carrying the stale value keeps the controlled select from
                  // rendering blank and names the broken binding; the user can switch to
                  // Unbound or a live document.
                  <option value={session.boundGrasshopperDocId} disabled>
                    Missing document ({session.boundGrasshopperDocId})
                  </option>
                ) : null}
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

        <div
          className={`composer ${dragging ? "dragging" : ""}`}
          onDragOver={handleDragOver}
          onDragLeave={() => setDragging(false)}
          onDrop={handleDrop}
        >
          {pending.length > 0 ? (
            <div className="attachment-strip" aria-label="Pending attachments">
              {pending.map((item) => (
                <span className="attachment-chip" key={item.id}>
                  <span className="chip-name" title={item.fileName}>
                    {item.fileName}
                  </span>
                  <span className="chip-size">{formatBytes(item.size)}</span>
                  <button
                    type="button"
                    className="chip-remove"
                    onClick={() => removeAttachment(item.id)}
                    disabled={sending}
                    aria-label={`Remove ${item.fileName}`}
                  >
                    ×
                  </button>
                </span>
              ))}
            </div>
          ) : null}
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={handleComposerKey}
            onPaste={handlePaste}
            placeholder={
              session.paused
                ? "Session is paused — resume it to continue"
                : session.role === "curator"
                  ? "Describe a document-care task — audit, cleanup, one-shot batch…"
                  : "Describe the next modeling change…"
            }
            aria-label="Message GPTino"
            rows={3}
            disabled={session.paused}
          />
          <input
            ref={fileInputRef}
            type="file"
            multiple
            hidden
            accept={ATTACHMENT_ACCEPT}
            onChange={handleFileInput}
            aria-hidden="true"
            tabIndex={-1}
          />
          <button
            type="button"
            className="attach-button"
            onClick={() => fileInputRef.current?.click()}
            disabled={sending || session.paused}
            aria-label="Attach files"
            title="Attach files — images, text, Markdown, JSON, CSV, PDF (max 4, 8 MB total). Paste or drop also works."
          >
            <Icon name="paperclip" />
          </button>
          <button
            type="button"
            className="send-button"
            onClick={() => void submit()}
            disabled={(!draft.trim() && pending.length === 0) || sending || session.paused}
            aria-label="Send instruction"
          >
            <Icon name="send" />
          </button>
        </div>
        {attachmentError ? (
          <div className="attachment-error" role="alert">
            {attachmentError}
          </div>
        ) : null}
        <div className="composer-hint">
          <div className="hint-keys">
            <span>Ctrl ↵ to send</span>
            <span>Shift ⇥ toggles Plan / Auto</span>
          </div>
          <UsageStatusLine usage={session.usage} limits={limits} />
        </div>
      </div>
    </section>
  );
}
