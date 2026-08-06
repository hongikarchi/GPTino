export type RuntimeHealth = "connected" | "degraded" | "disconnected";
export type SessionStatus =
  | "working"
  | "drafting"
  | "queued"
  | "verifying"
  | "paused"
  | "blocked"
  | "idle";
// Reasoning-effort level (ascending). The session field is still named modelProfile on the wire for
// back-compat, but it carries the effort — set directly (no adaptive routing), clamped to the model.
export type ModelProfile = "low" | "medium" | "high" | "xhigh" | "max" | "ultra";
export type MessageRole = "user" | "assistant" | "system";

export interface ChatMessage {
  id: string;
  role: MessageRole;
  content: string;
  createdAt: string;
  pending?: boolean;
}

export interface SessionJob {
  id: string;
  title: string;
  phase: string;
  progress?: number;
  baseRevision?: number;
}

export interface SessionActivity {
  at: string;
  kind: string;
  summary: string;
  ok: boolean;
  durationMs: number;
}

export interface ModelInfo {
  id: string;
  model: string;
  displayName: string;
  description: string;
  isDefault: boolean;
  reasoningEfforts: string[];
}

export interface SessionUsage {
  /** Cumulative tokens spent by this session's agent. Server may send explicit nulls. */
  totalTokens?: number | null;
  /** Model context window size, when the backend reports it. */
  contextWindow?: number | null;
  /** Tokens currently occupying the context window (last turn footprint). */
  contextUsedTokens?: number | null;
  /** Provider rate-limit windows, e.g. { label: "5h", usedPercent: 34 }. */
  rateLimits?: { label: string; usedPercent: number; resetsAt?: string | null }[] | null;
}

/** One account-scoped provider rate-limit window, e.g. { label: "5h", usedPercent: 34 }. */
export interface CodexLimitWindow {
  label: string;
  usedPercent: number;
  resetsAt?: string | null;
}

/** Freshest account rate limits seen on any session's turn (rate limits are account-, not thread-scoped). */
export interface CodexLimits {
  updatedAt?: string | null;
  windows: CodexLimitWindow[];
}

/** One Grasshopper document registered with the runtime. `id` is the durable 16-hex docKey. */
export interface GrasshopperDocInfo {
  id: string;
  file: string;
}

/**
 * Eventually-consistent Rhino<->GH data-flow summary for one GH doc: what its parameters
 * reference from the Rhino document (missing = broken references silently emitting empty data)
 * and how many stamped bakes it produced. Counts are stamped with the per-doc revision they were
 * observed at — a discovery hint, never a write precondition.
 */
export interface DocDataFlow {
  /** docKey of the GH doc this summary describes. */
  docId: string;
  referenceCount: number;
  missingReferenceCount: number;
  bakeCount: number;
  revision: number;
  observedAt: string;
}

export interface DataFlowDetailObject {
  rhinoObjectId: string;
  exists: boolean;
  layerFullPath?: string | null;
}

export interface DataFlowDetailParameter {
  parameterId: string;
  nickName: string;
  parameterType: string;
  objects: DataFlowDetailObject[];
}

export interface DataFlowBakeGroup {
  sourceDocKey?: string | null;
  bakeFamily?: string | null;
  count: number;
  objectIds: string[];
}

/**
 * What the agent understood the request to be, framed before the work starts. The user answers
 * it (approve / narrow / correct), and the same criteria come back as the self-score at the end.
 */
export interface GoalCard {
  status: "proposing" | "confirmed" | "rejected" | "scored";
  objective: string;
  criteria: string[];
  assumptions?: string[];
  outOfScope?: string[];
  options?: GoalOption[];
  chosenOption?: string | null;
  scores?: { criterion: string; passed: boolean; evidence: string }[];
}

export interface GoalOption {
  id: string;
  label: string;
  detail?: string | null;
  /** Rhino objects this option is about — choosing it can show them in the viewport. */
  objectIds?: string[] | null;
}

/** Destructive fixes to the user's own geometry, listed for item-by-item approval. */
export interface ApprovalCard {
  status: "proposing" | "granted" | "rejected";
  summary: string;
  items: ApprovalItem[];
  grantId?: string | null;
  approvedItemIds?: string[] | null;
}

export interface ApprovalItem {
  id: string;
  label: string;
  measure?: string | null;
  /** Each target pins an object to the fingerprint that was audited. */
  targets: { objectId: string; fingerprint: string }[];
  choices?: string[] | null;
}

/** Viewport focus modes: select+zoom, or additionally hide/lock everything else, or put it back. */
export type FocusMode = "select" | "isolate" | "lock" | "restore";

export interface FocusResult {
  selectedCount: number;
  /** Findings can outlive their objects — a deleted target is reported, never silently dropped. */
  missingCount: number;
  hiddenCount: number;
  lockedCount: number;
  restored: boolean;
  fingerprint: string;
}

/** POST /canvas/focus result: select + frame Grasshopper components (no isolate/lock/restore). */
export interface CanvasFocusResult {
  selectedCount: number;
  /** Chat can reference a component that was since deleted — reported, never silently dropped. */
  missingCount: number;
  fingerprint: string;
}

/** On-demand GET /data-flow payload; writerActive=true means retry after the queue drains. */
export interface DataFlowDetail {
  docId?: string;
  revision?: number;
  observedAt?: string;
  writerActive?: boolean;
  message?: string;
  references?: {
    grasshopperDocumentId: string;
    referenceCount: number;
    missingCount: number;
    parameters: DataFlowDetailParameter[];
  };
  bakes?: {
    totalStamped: number;
    groups: DataFlowBakeGroup[];
  };
}

export interface GptinoSession {
  id: string;
  title: string;
  summary?: string;
  status: SessionStatus;
  modelProfile: ModelProfile;
  pinnedModel?: string | null;
  goalEnabled?: boolean;
  /** Raw goal-card JSON from the server (parsed by the card component). */
  goalCard?: string | null;
  /** Raw approval-card JSON from the server (parsed by the card component). */
  approvalCard?: string | null;
  backend?: string;
  effectiveModel?: string;
  reasoning?: string;
  effectiveProfile?: string;
  routingTaskClass?: string;
  routingReason?: string;
  routingError?: string;
  paused: boolean;
  terminalOpen?: boolean;
  unread?: number;
  currentActivity?: string | null;
  activity?: SessionActivity[];
  messages: ChatMessage[];
  job?: SessionJob;
  usage?: SessionUsage;
  /** docKey of the GH doc this session writes to; null = unbound (default doc when only one exists). */
  boundGrasshopperDocId?: string | null;
}

export type QueueItemState = "ready" | "waiting" | "applying" | "verifying";

export interface QueueItem {
  id: string;
  sessionId: string;
  title: string;
  state: QueueItemState;
  resource?: string | null;
  waitingFor?: string | null;
  target?: "rhino" | "grasshopper" | "both" | null;
  /** docKey of the GH doc this job writes to; null when the job is not doc-attributable. */
  targetDocId?: string | null;
}

export interface RuntimeConflict {
  id: string;
  title: string;
  detail: string;
  sessionIds: string[];
  resource?: string | null;
  /** Server-suggested way out of the conflict, shown as the "Solution" half of the card. */
  resolution?: string | null;
  observedAt?: string | null;
}

export interface CurrentWriter {
  sessionId: string;
  jobId: string;
  label: string;
  phase: string;
  startedAt: string;
  progress?: number;
}

export interface CurrentSelection {
  rhinoObjectCount: number;
  rhinoObjectIds: string[];
  activeLayer?: string | null;
  grasshopperObjectCount?: number;
  grasshopperObjects?: { id: string; nickName: string }[];
  /** docKey of the GH doc the grasshopper selection belongs to; null when not attributable. */
  docId?: string | null;
  observedAt: string;
}

export type CodexAuthStatus = "logged-in" | "logged-out" | "cli-missing" | "unknown";

export interface CodexAuth {
  status: CodexAuthStatus;
  detail?: string;
}

export interface RuntimeState {
  projectId: string;
  projectName: string;
  rhinoFile: string;
  /**
   * The bound Grasshopper file, or null when no definition is open. Null is a normal state, not a
   * failure: Rhino-only document work needs no definition. Model and Data are the only tabs that
   * need one.
   */
  grasshopperFile: string | null;
  /** All registered GH docs; null/absent = legacy single-doc server (fall back to grasshopperFile). */
  grasshopperDocs?: GrasshopperDocInfo[] | null;
  health: RuntimeHealth;
  healthDetail?: string;
  revision: number;
  gitRevision?: number | null;
  orderVersion: number;
  paused: boolean;
  writer?: CurrentWriter | null;
  sessions: GptinoSession[];
  queue: QueueItem[];
  conflicts: RuntimeConflict[];
  currentSelection?: CurrentSelection | null;
  /** Per-doc data-flow summaries; null/absent until the first backend refresh (or legacy server). */
  dataFlow?: DocDataFlow[] | null;
  /** Stamped bakes whose source doc predates provenance stamping — honest bucket, never guessed. */
  unattributedBakeCount?: number;
  contextFolder?: string | null;
  codexAuth?: CodexAuth;
  codexLimits?: CodexLimits | null;
  lastUpdatedAt: string;
}

/** One session summary inside a read-only archived project root. */
export interface ArchiveSession {
  id: string;
  name: string;
  state: string;
  updatedAt: string;
  messageCount: number;
  /** Soft-deleted (still recoverable). For the current project these can be restored or purged here. */
  deleted: boolean;
}

/** One GPTino project data root on this machine, current or orphaned by a crash/path change. */
export interface ArchiveProject {
  fingerprint: string;
  projectName?: string | null;
  rhinoFile?: string | null;
  grasshopperFile?: string | null;
  createdAt?: string | null;
  lastActivityAt?: string | null;
  sessionCount: number;
  current: boolean;
  available: boolean;
  sessions: ArchiveSession[];
}

/** A transcript row read straight from an archived root's runtime database. */
export interface ArchiveMessage {
  id: number;
  role: string;
  content: string;
  phase?: string | null;
  createdAt: string;
}

/** A soft-deleted session, listed in the trash for restore or permanent removal. */
export interface DeletedSession {
  id: string;
  name: string;
  state: string;
  updatedAt: string;
}

export interface SessionOrderRequest {
  orderedSessionIds: string[];
  orderVersion: number;
}

/** A file the composer attaches to a message, carried as Base64 over the loopback API. */
export interface MessageAttachment {
  fileName: string;
  mediaType: string;
  dataBase64: string;
}

/**
 * A selection the user pinned in the composer (like an attachment). Travels with the message as its
 * authoritative operand — the objects to act on — decoupled from the live selection so the user can
 * pin and keep working. Ids are captured at pin time; the agent resolves fingerprints live.
 */
export interface PinnedSelection {
  rhinoObjectIds?: string[];
  grasshopperObjects?: { id: string; label?: string }[];
}

export interface MessageRequest {
  content: string;
  clientMessageId?: string;
  attachments?: MessageAttachment[];
  pinnedSelection?: PinnedSelection;
}
