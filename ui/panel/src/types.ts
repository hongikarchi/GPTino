export type RuntimeHealth = "connected" | "degraded" | "disconnected";
export type SessionStatus =
  | "working"
  | "drafting"
  | "queued"
  | "verifying"
  | "paused"
  | "blocked"
  | "idle";
export type SessionMode = "plan" | "auto";
export type ModelProfile = "auto" | "fast" | "standard" | "deep";
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

/** One Grasshopper document registered with the runtime. `id` is the durable 16-hex docKey. */
export interface GrasshopperDocInfo {
  id: string;
  file: string;
}

export interface GptinoSession {
  id: string;
  title: string;
  summary?: string;
  status: SessionStatus;
  mode: SessionMode;
  modelProfile: ModelProfile;
  pinnedModel?: string | null;
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
  grasshopperFile: string;
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
  contextFolder?: string | null;
  codexAuth?: CodexAuth;
  lastUpdatedAt: string;
}

/** One session summary inside a read-only archived project root. */
export interface ArchiveSession {
  id: string;
  name: string;
  state: string;
  updatedAt: string;
  messageCount: number;
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

export interface MessageRequest {
  content: string;
  clientMessageId?: string;
  attachments?: MessageAttachment[];
}
