import type { GptinoSession, GrasshopperDocInfo, RuntimeState } from "../types";

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
  /** GH docKey for multi-doc grasshopper nodes; absent on the rhino node and the legacy single GH node. */
  docId?: string;
  detail?: string;
  /** Full, untruncated hover text when the rendered detail line is clipped. */
  tooltip?: string;
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

const SESSION_W = 208;
const SESSION_H = 86;
const SESSION_X = 24;
const SESSION_GAP = 14;
const ORCH_W = 210;
const ORCH_H = 116;
const ORCH_X = 300;
const DOC_W = 190;
const DOC_H = 70;
const DOC_X = 578;
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
  // Legacy servers omit grasshopperDocs (or send null) — render the single
  // doc:gh node from grasshopperFile exactly as before multi-GH support.
  const ghDocs: GrasshopperDocInfo[] | null =
    state.grasshopperDocs != null && state.grasshopperDocs.length > 0 ? state.grasshopperDocs : null;
  const ghCount = ghDocs?.length ?? 1;
  const docCount = 1 + ghCount;
  const docStackH = DOC_H * docCount + DOC_GAP * (docCount - 1);
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
      sublabel: session.currentActivity ?? session.job?.phase ?? session.summary,
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
      live: state.writer != null,
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
      ? `${selection.rhinoObjectCount} selected${selection.activeLayer ? ` · layer ${selection.activeLayer}` : ""}`
      : undefined,
    tooltip: selection
      ? `${selection.rhinoObjectCount} object${selection.rhinoObjectCount === 1 ? "" : "s"} selected${selection.activeLayer ? `\nActive layer: ${selection.activeLayer}` : ""}`
      : undefined,
    docTarget: "rhino",
  };
  const ghSelected = selection?.grasshopperObjectCount;
  const ghNickNames = (selection?.grasshopperObjects ?? [])
    .map(({ nickName }) => nickName)
    .filter(Boolean);
  const ghDetail =
    ghSelected !== undefined
      ? `${ghSelected} selected${ghNickNames.length > 0 ? ` · ${ghNickNames[0]}${ghSelected > 1 ? "…" : ""}` : ""}`
      : undefined;
  const ghTooltip =
    ghSelected !== undefined
      ? `${ghSelected} component${ghSelected === 1 ? "" : "s"} selected${ghNickNames.length > 0 ? `\n${ghNickNames.join(", ")}` : ""}`
      : undefined;
  // The GH selection badge lands on the doc the selection was observed in. An
  // unattributed selection still lands on an only doc (the default-target rule);
  // with several docs and no attribution it is shown nowhere rather than wrongly.
  const badgeDocId = ghDocs ? (selection?.docId ?? (ghDocs.length === 1 ? ghDocs[0].id : null)) : null;
  const ghDocNodes: GraphNode[] = ghDocs
    ? ghDocs.map((doc, index) => ({
        id: `doc:gh:${doc.id}`,
        kind: "doc" as const,
        x: DOC_X,
        y: docStartY + (index + 1) * (DOC_H + DOC_GAP),
        w: DOC_W,
        h: DOC_H,
        label: "Grasshopper",
        sublabel: shortFile(doc.file),
        detail: badgeDocId === doc.id ? ghDetail : undefined,
        tooltip: badgeDocId === doc.id ? ghTooltip : undefined,
        docTarget: "grasshopper" as const,
        docId: doc.id,
      }))
    : [
        {
          id: "doc:gh",
          kind: "doc" as const,
          x: DOC_X,
          y: docStartY + DOC_H + DOC_GAP,
          w: DOC_W,
          h: DOC_H,
          label: "Grasshopper",
          sublabel: shortFile(state.grasshopperFile),
          detail: ghDetail,
          tooltip: ghTooltip,
          docTarget: "grasshopper" as const,
        },
      ];
  nodes.push(rhinoDoc, ...ghDocNodes);

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
  // only that document's wire animates; without target info all animate. With
  // several GH docs the job's targetDocId narrows the animation to one GH node,
  // falling back to every GH wire when the backend cannot attribute the doc.
  const writerQueueItem = state.writer
    ? state.queue.find((item) => item.id === state.writer?.jobId)
    : undefined;
  const writerTarget = writerQueueItem?.target;
  const writerTargetDocId = writerQueueItem?.targetDocId ?? null;
  const animateRhino = state.writer != null && (writerTarget == null || writerTarget === "rhino" || writerTarget === "both");
  const animateGh = state.writer != null && (writerTarget == null || writerTarget === "grasshopper" || writerTarget === "both");

  const commitDocs = [rhinoDoc, ...ghDocNodes];
  commitDocs.forEach((doc, index) => {
    const animated =
      doc.docTarget === "rhino"
        ? animateRhino
        : animateGh && (writerTargetDocId == null || doc.docId === undefined || writerTargetDocId === doc.docId);
    const x1 = ORCH_X + ORCH_W;
    // One orchestrator output port per document, distributed down the hub's
    // right edge — the same formula as the session input ports on the left.
    const y1 = orchY + (ORCH_H * (index + 1)) / (commitDocs.length + 1);
    const x2 = doc.x;
    const y2 = doc.y + doc.h / 2;
    const dx = clamp((x2 - x1) / 2, 48, 120);
    edges.push({
      id: doc.docTarget === "rhino" ? "commit:rhino" : doc.docId !== undefined ? `commit:gh:${doc.docId}` : "commit:grasshopper",
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
  });

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
