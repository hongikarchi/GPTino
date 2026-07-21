import type { GptinoSession, RuntimeState } from "../types";

export type GraphNodeKind = "session" | "orchestrator" | "doc";
export type WireKind = "active" | "queued" | "blocked" | "paused" | "idle" | "commit" | "conflict";
export type DocTarget = "rhino" | "grasshopper";

export interface OrchestratorInfo {
  live: boolean;
  paused: boolean;
  queueDepth: number;
  revision: number;
  writerSessionTitle?: string;
  writerPhase?: string;
  writerStartedAt?: string;
}

export interface GraphNode {
  id: string;
  kind: GraphNodeKind;
  x: number;
  y: number;
  w: number;
  h: number;
  label: string;
  sublabel?: string;
  rank?: number;
  session?: GptinoSession;
  warning?: string;
  docTarget?: DocTarget;
  detail?: string;
  orchestrator?: OrchestratorInfo;
}

export interface GraphEdge {
  id: string;
  from: string;
  to: string;
  kind: WireKind;
  animated: boolean;
  label?: string;
  title?: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  path: string;
  midX: number;
  midY: number;
}

export interface GraphModel {
  nodes: GraphNode[];
  edges: GraphEdge[];
  width: number;
  height: number;
}

const SESSION_W = 176;
const SESSION_H = 72;
const SESSION_X = 24;
const SESSION_GAP = 14;
const ORCH_W = 190;
const ORCH_H = 118;
const ORCH_X = 320;
const DOC_W = 160;
const DOC_H = 64;
const DOC_X = 630;
const DOC_GAP = 24;
const MARGIN = 24;
const CANVAS_W = DOC_X + DOC_W + MARGIN;

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

export function wirePath(x1: number, y1: number, x2: number, y2: number): string {
  const dx = clamp((x2 - x1) / 2, 48, 120);
  return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`;
}

function conflictPath(x: number, y1: number, y2: number): string {
  const bow = 56;
  return `M ${x} ${y1} C ${x - bow} ${y1}, ${x - bow} ${y2}, ${x} ${y2}`;
}

/** Midpoint of a cubic bezier at t = 0.5: (P0 + 3·P1 + 3·P2 + P3) / 8. */
function cubicMid(p0: number, p1: number, p2: number, p3: number): number {
  return (p0 + 3 * p1 + 3 * p2 + p3) / 8;
}

const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

export function deriveGraph(state: RuntimeState): GraphModel {
  const sessions = state.sessions;
  const sessionCount = sessions.length;
  const sessionStackH = sessionCount > 0 ? sessionCount * SESSION_H + (sessionCount - 1) * SESSION_GAP : 0;
  const docStackH = DOC_H * 2 + DOC_GAP;
  const contentH = Math.max(sessionStackH, ORCH_H, docStackH);
  const height = contentH + MARGIN * 2;

  const sessionStartY = MARGIN + (contentH - sessionStackH) / 2;
  const orchY = MARGIN + (contentH - ORCH_H) / 2;
  const docStartY = MARGIN + (contentH - docStackH) / 2;

  const nodes: GraphNode[] = [];
  const edges: GraphEdge[] = [];

  const singleSessionWarnings = new Map<string, string>();
  for (const conflict of state.conflicts) {
    if (conflict.sessionIds.length === 1) {
      const existing = singleSessionWarnings.get(conflict.sessionIds[0]);
      singleSessionWarnings.set(
        conflict.sessionIds[0],
        existing ? `${existing}\n${conflict.title}: ${conflict.detail}` : `${conflict.title}: ${conflict.detail}`,
      );
    }
  }

  const sessionNodeById = new Map<string, GraphNode>();
  sessions.forEach((session, index) => {
    const node: GraphNode = {
      id: `session:${session.id}`,
      kind: "session",
      x: SESSION_X,
      y: sessionStartY + index * (SESSION_H + SESSION_GAP),
      w: SESSION_W,
      h: SESSION_H,
      label: session.title,
      sublabel: session.job?.phase ?? session.summary,
      rank: index + 1,
      session,
      warning: singleSessionWarnings.get(session.id),
    };
    nodes.push(node);
    sessionNodeById.set(session.id, node);
  });

  const writerSession = state.writer
    ? sessions.find((session) => session.id === state.writer?.sessionId)
    : undefined;

  nodes.push({
    id: "orchestrator",
    kind: "orchestrator",
    x: ORCH_X,
    y: orchY,
    w: ORCH_W,
    h: ORCH_H,
    label: "Orchestrator",
    sublabel: `Queue ${state.queue.length}`,
    orchestrator: {
      live: state.writer !== undefined,
      paused: state.paused,
      queueDepth: state.queue.length,
      revision: state.revision,
      writerSessionTitle: writerSession?.title ?? state.writer?.label,
      writerPhase: state.writer?.phase,
      writerStartedAt: state.writer?.startedAt,
    },
  });

  const selection = state.currentSelection;
  const rhinoDoc: GraphNode = {
    id: "doc:rhino",
    kind: "doc",
    x: DOC_X,
    y: docStartY,
    w: DOC_W,
    h: DOC_H,
    label: "Rhino",
    sublabel: shortFile(state.rhinoFile),
    detail: selection
      ? `${selection.rhinoObjectCount} selected${selection.activeLayer ? ` · ${selection.activeLayer}` : ""}`
      : undefined,
    docTarget: "rhino",
  };
  const ghDoc: GraphNode = {
    id: "doc:gh",
    kind: "doc",
    x: DOC_X,
    y: docStartY + DOC_H + DOC_GAP,
    w: DOC_W,
    h: DOC_H,
    label: "Grasshopper",
    sublabel: shortFile(state.grasshopperFile),
    docTarget: "grasshopper",
  };
  nodes.push(rhinoDoc, ghDoc);

  // Session → orchestrator wires. One orchestrator input port per session,
  // distributed down the hub's left edge in priority order.
  const pendingQueue = state.queue.filter((item) => item.state !== "applying" && item.state !== "verifying");
  const queuePosition = new Map<string, number>();
  pendingQueue.forEach((item, index) => {
    if (!queuePosition.has(item.sessionId)) queuePosition.set(item.sessionId, index + 1);
  });
  const queuedSessionIds = new Set(state.queue.map((item) => item.sessionId));
  const executingSessionIds = new Set(
    state.queue
      .filter((item) => item.state === "applying" || item.state === "verifying")
      .map((item) => item.sessionId),
  );

  sessions.forEach((session, index) => {
    const node = sessionNodeById.get(session.id);
    if (!node) return;
    const x1 = node.x + node.w;
    const y1 = node.y + node.h / 2;
    const x2 = ORCH_X;
    const y2 = orchY + (ORCH_H * (index + 1)) / (sessionCount + 1);

    let kind: WireKind;
    let label: string | undefined;
    if (state.writer?.sessionId === session.id || executingSessionIds.has(session.id)) {
      kind = "active";
    } else if (session.status === "blocked") {
      kind = "blocked";
    } else if (session.paused) {
      kind = "paused";
    } else if (queuedSessionIds.has(session.id)) {
      kind = "queued";
      const position = queuePosition.get(session.id);
      label = position === undefined ? undefined : `#${position}`;
    } else {
      kind = "idle";
    }

    const dx = clamp((x2 - x1) / 2, 48, 120);
    edges.push({
      id: `wire:${session.id}`,
      from: node.id,
      to: "orchestrator",
      kind,
      animated: kind === "active",
      label,
      title: session.job?.title ?? session.summary,
      x1,
      y1,
      x2,
      y2,
      path: wirePath(x1, y1, x2, y2),
      midX: cubicMid(x1, x1 + dx, x2 - dx, x2),
      midY: cubicMid(y1, y1, y2, y2),
    });
  });

  // Orchestrator → document wires. When the executing job declares a target,
  // only that document's wire animates; without target info both animate.
  const writerQueueItem = state.writer
    ? state.queue.find((item) => item.id === state.writer?.jobId)
    : undefined;
  const writerTarget = writerQueueItem?.target;
  const animateRhino = state.writer !== undefined && (writerTarget === undefined || writerTarget === "rhino" || writerTarget === "both");
  const animateGh = state.writer !== undefined && (writerTarget === undefined || writerTarget === "grasshopper" || writerTarget === "both");

  for (const [doc, animated] of [
    [rhinoDoc, animateRhino],
    [ghDoc, animateGh],
  ] as const) {
    const x1 = ORCH_X + ORCH_W;
    const y1 = orchY + (doc.docTarget === "rhino" ? ORCH_H / 3 : (ORCH_H * 2) / 3);
    const x2 = doc.x;
    const y2 = doc.y + doc.h / 2;
    const dx = clamp((x2 - x1) / 2, 48, 120);
    edges.push({
      id: `commit:${doc.docTarget}`,
      from: "orchestrator",
      to: doc.id,
      kind: "commit",
      animated,
      title: animated ? state.writer?.label : undefined,
      x1,
      y1,
      x2,
      y2,
      path: wirePath(x1, y1, x2, y2),
      midX: cubicMid(x1, x1 + dx, x2 - dx, x2),
      midY: cubicMid(y1, y1, y2, y2),
    });
  }

  // Pairwise conflicts arc along the left of the session column.
  for (const conflict of state.conflicts) {
    if (conflict.sessionIds.length !== 2) continue;
    const a = sessionNodeById.get(conflict.sessionIds[0]);
    const b = sessionNodeById.get(conflict.sessionIds[1]);
    if (!a || !b) continue;
    const y1 = a.y + a.h / 2;
    const y2 = b.y + b.h / 2;
    edges.push({
      id: `conflict:${conflict.id}`,
      from: a.id,
      to: b.id,
      kind: "conflict",
      animated: false,
      title: `${conflict.title}: ${conflict.detail}`,
      x1: SESSION_X,
      y1,
      x2: SESSION_X,
      y2,
      path: conflictPath(SESSION_X, y1, y2),
      midX: SESSION_X - 42,
      midY: cubicMid(y1, y1, y2, y2),
    });
  }

  return { nodes, edges, width: CANVAS_W, height };
}
