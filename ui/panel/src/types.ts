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

export interface ModelInfo {
  id: string;
  model: string;
  displayName: string;
  description: string;
  isDefault: boolean;
  reasoningEfforts: string[];
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
  messages: ChatMessage[];
  job?: SessionJob;
}

export type QueueItemState = "ready" | "waiting" | "applying" | "verifying";

export interface QueueItem {
  id: string;
  sessionId: string;
  title: string;
  state: QueueItemState;
  resource?: string;
  waitingFor?: string;
  target?: "rhino" | "grasshopper" | "both";
}

export interface RuntimeConflict {
  id: string;
  title: string;
  detail: string;
  sessionIds: string[];
  resource?: string;
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
  observedAt: string;
}

export interface RuntimeState {
  projectId: string;
  projectName: string;
  rhinoFile: string;
  grasshopperFile: string;
  health: RuntimeHealth;
  healthDetail?: string;
  revision: number;
  gitRevision?: number;
  orderVersion: number;
  paused: boolean;
  writer?: CurrentWriter;
  sessions: GptinoSession[];
  queue: QueueItem[];
  conflicts: RuntimeConflict[];
  currentSelection?: CurrentSelection | null;
  lastUpdatedAt: string;
}

export interface SessionOrderRequest {
  orderedSessionIds: string[];
  orderVersion: number;
}

export interface MessageRequest {
  content: string;
  clientMessageId?: string;
}
