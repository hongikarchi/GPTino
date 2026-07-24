import { useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent } from "react";
import type { RuntimeState } from "../types";
import type { GraphEdge, GraphNode } from "../graph/deriveGraph";
import { deriveGraph } from "../graph/deriveGraph";

interface SessionCanvasProps {
  runtime: RuntimeState;
  selectedId: string | null;
  onSelect(id: string): void;
  onReorder(sourceId: string, targetId: string): void;
  onPauseToggle(id: string, paused: boolean): void;
}

interface ViewBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface NodeDrag {
  sessionId: string;
  pointerId: number;
  startClientY: number;
  nodeCenterY: number;
}

const MIN_ZOOM = 0.5;
const MAX_ZOOM = 2.5;
const DRAG_THRESHOLD = 6;
/* The default view never renders below 1:1 (CSS px per canvas unit), so the 10px+
   SVG text classes keep their full on-screen size on narrow panels; the canvas
   pans (with a right-edge fade cue) instead of shrinking. Double-click toggles
   between this readable default and a fit-everything view. */
const MIN_READABLE_SCALE = 1;
/* Extra canvas units above the content in the clamped view so the absolutely
   positioned toolbar does not cover the first node's title row. */
const CLAMPED_TOP_HEADROOM = 16;

const truncate = (value: string, max: number) =>
  value.length > max ? `${value.slice(0, max - 1)}…` : value;

/* Character counts cannot bound pixel width once CJK text is involved (a Hangul
   glyph is ~2× a Latin one), so node labels truncate by half-width columns. */
const columnsOf = (value: string): number => {
  let columns = 0;
  for (const ch of value) {
    columns += ch.charCodeAt(0) > 0x2e7f ? 2 : 1;
  }
  return columns;
};

const truncateColumns = (value: string, maxColumns: number): string => {
  if (columnsOf(value) <= maxColumns) return value;
  let result = "";
  let columns = 0;
  for (const ch of value) {
    const width = ch.charCodeAt(0) > 0x2e7f ? 2 : 1;
    if (columns + width > maxColumns - 1) break;
    result += ch;
    columns += width;
  }
  return `${result}…`;
};

function elapsedLabel(startedAt: string, nowMs: number): string {
  const started = Date.parse(startedAt);
  if (Number.isNaN(started)) return "";
  const totalSeconds = Math.max(0, Math.floor((nowMs - started) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

function sessionTooltip(node: GraphNode): string {
  const session = node.session;
  if (!session) return node.label;
  const lines = [session.title];
  if (session.summary) lines.push(session.summary);
  if (session.currentActivity) lines.push(`Now: ${session.currentActivity}`);
  if (session.job) lines.push(`Job: ${session.job.title} — ${session.job.phase}`);
  if (session.effectiveModel) {
    lines.push(`Model: ${session.effectiveModel}${session.reasoning ? ` (${session.reasoning})` : ""}`);
  }
  if (session.pinnedModel) lines.push(`Pinned: ${session.pinnedModel}`);
  if (session.backend) lines.push(`Backend: ${session.backend}`);
  if (session.routingReason) lines.push(`Routing: ${session.routingReason}`);
  if (node.warning) lines.push(node.warning);
  lines.push("Drag vertically to change priority.");
  return lines.join("\n");
}

function Wire({ edge }: { edge: GraphEdge }) {
  return (
    <g className={`wire wire-${edge.kind}`}>
      {edge.title ? <title>{edge.title}</title> : null}
      <path className="wire-under" d={edge.path} />
      <path className="wire-color" d={edge.path} />
      {edge.animated ? <path className="wire-flow" d={edge.path} /> : null}
      <circle className="wire-port" cx={edge.x1} cy={edge.y1} r={3.2} />
      <circle className="wire-port" cx={edge.x2} cy={edge.y2} r={3.2} />
      {edge.label ? (
        <g className="wire-chip" transform={`translate(${edge.midX}, ${edge.midY})`}>
          <rect x={-13} y={-9} width={26} height={18} rx={9} />
          <text x={0} y={3.5}>{edge.label}</text>
        </g>
      ) : null}
    </g>
  );
}

function SessionNode({
  node,
  selected,
  dragOffset,
  onSelect,
  onNodePointerDown,
  onPauseToggle,
}: {
  node: GraphNode;
  selected: boolean;
  dragOffset: number;
  onSelect(id: string): void;
  onNodePointerDown(event: ReactPointerEvent<SVGGElement>, sessionId: string): void;
  onPauseToggle(id: string, paused: boolean): void;
}) {
  const session = node.session;
  if (!session) return null;

  const handleKeyDown = (event: ReactKeyboardEvent<SVGGElement>) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(session.id);
    }
  };

  return (
    <g
      className={`gnode gnode-session status-${session.status}${selected ? " selected" : ""}${session.paused ? " paused" : ""}${node.warning ? " warning" : ""}${dragOffset !== 0 ? " dragging" : ""}`}
      transform={`translate(${node.x}, ${node.y + dragOffset})`}
      role="button"
      tabIndex={0}
      aria-label={`Session ${session.title}`}
      onKeyDown={handleKeyDown}
      onPointerDown={(event) => onNodePointerDown(event, session.id)}
    >
      <title>{sessionTooltip(node)}</title>
      <rect className="gnode-box" width={node.w} height={node.h} rx={8} />
      <text className="gnode-rank" x={10} y={20}>{node.rank}</text>
      <text className="gnode-title" x={30} y={20}>
        {truncateColumns(session.title, session.terminalOpen ? 20 : node.warning ? 23 : 25)}
      </text>
      {session.terminalOpen ? (
        <text className="gnode-terminal" x={node.w - 32} y={20}>{"❯_"}</text>
      ) : null}
      {node.warning ? (
        <text className="gnode-warning" x={node.w - 13} y={20}>!</text>
      ) : null}
      <circle className="gnode-status-dot" cx={14} cy={39} r={3.5} />
      <text className="gnode-status" x={24} y={43}>{session.status}</text>
      <text className="gnode-chips" x={10} y={61}>
        {truncate(`${session.pinnedModel ?? session.modelProfile} · ${session.mode}`, 28)}
      </text>
      {node.sublabel ? (
        <text className="gnode-sub" x={10} y={77}>{truncateColumns(node.sublabel, 33)}</text>
      ) : null}
      {selected ? (
        <g
          className="gnode-action"
          role="button"
          aria-label={session.paused ? "Resume session" : "Pause session"}
          onClick={(event) => {
            event.stopPropagation();
            onPauseToggle(session.id, !session.paused);
          }}
          onPointerDown={(event) => event.stopPropagation()}
        >
          <title>{session.paused ? "Resume" : "Pause"}</title>
          <circle cx={node.w - 18} cy={node.h - 13} r={9} />
          {session.paused ? (
            <path className="gnode-action-glyph" d={`M ${node.w - 21} ${node.h - 17.5} l 7 4.5 l -7 4.5 Z`} />
          ) : (
            <>
              <line className="gnode-action-glyph" x1={node.w - 20.5} y1={node.h - 17} x2={node.w - 20.5} y2={node.h - 9} />
              <line className="gnode-action-glyph" x1={node.w - 15.5} y1={node.h - 17} x2={node.w - 15.5} y2={node.h - 9} />
            </>
          )}
        </g>
      ) : null}
    </g>
  );
}

function OrchestratorNode({ node, nowMs }: { node: GraphNode; nowMs: number }) {
  const info = node.orchestrator;
  if (!info) return null;
  const elapsed = info.live && info.writerStartedAt ? elapsedLabel(info.writerStartedAt, nowMs) : "";

  return (
    <g
      className={`gnode gnode-orchestrator${info.live ? " live" : ""}${info.paused ? " paused" : ""}`}
      transform={`translate(${node.x}, ${node.y})`}
    >
      <title>
        {info.paused
          ? "Single-writer broker — paused"
          : info.live
            ? `Single-writer broker — executing for ${info.writerSessionTitle ?? "a session"}`
            : "Single-writer broker — idle"}
      </title>
      <rect className="gnode-box" width={node.w} height={node.h} rx={10} />
      <circle className="gnode-lamp" cx={17} cy={20} r={4.5} />
      <text className="gnode-heading" x={30} y={25}>ORCHESTRATOR</text>
      {info.paused ? (
        <text className="gnode-writer" x={14} y={56}>Paused</text>
      ) : info.live ? (
        <>
          <text className="gnode-writer" x={14} y={54}>
            {truncateColumns(info.writerSessionTitle ?? "Executing", 30)}
          </text>
          <text className="gnode-phase" x={14} y={70}>
            {truncateColumns(`${info.writerPhase ?? "Executing"}${elapsed ? ` · ${elapsed}` : ""}`, 34)}
          </text>
        </>
      ) : (
        <text className="gnode-writer idle" x={14} y={56}>Idle — waiting for jobs</text>
      )}
      <text className="gnode-footer" x={14} y={node.h - 12}>
        {`QUEUE ${info.queueDepth}`}
      </text>
    </g>
  );
}

function DocNode({ node }: { node: GraphNode }) {
  const tooltip = [`${node.label} — ${node.sublabel ?? ""}`, node.tooltip ?? node.detail]
    .filter(Boolean)
    .join("\n");
  return (
    <g className={`gnode gnode-doc doc-${node.docTarget}`} transform={`translate(${node.x}, ${node.y})`}>
      <title>{tooltip}</title>
      <rect className="gnode-box" width={node.w} height={node.h} rx={8} />
      <rect className="gnode-doc-mark" x={10} y={node.h / 2 - 14} width={28} height={28} rx={6} />
      <text className="gnode-doc-glyph" x={24} y={node.h / 2 + 4}>
        {node.docTarget === "rhino" ? "R" : "GH"}
      </text>
      <text className="gnode-title" x={50} y={node.detail ? 22 : 30}>{node.label}</text>
      <text className="gnode-sub" x={50} y={node.detail ? 36 : 46}>
        {truncateColumns(node.sublabel ?? "", 24)}
      </text>
      {node.detail ? (
        <text className="gnode-detail" x={50} y={51}>{truncate(node.detail, 22)}</text>
      ) : null}
    </g>
  );
}

export function SessionCanvas({
  runtime,
  selectedId,
  onSelect,
  onReorder,
  onPauseToggle,
}: SessionCanvasProps) {
  const model = useMemo(() => deriveGraph(runtime), [runtime]);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const [viewBox, setViewBox] = useState<ViewBox | null>(null);
  const panState = useRef<{ pointerId: number; startX: number; startY: number; origin: ViewBox } | null>(null);
  const nodeDrag = useRef<NodeDrag | null>(null);
  const [dragState, setDragState] = useState<{ sessionId: string; dy: number } | null>(null);
  const draggedRef = useRef(false);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [containerSize, setContainerSize] = useState<{ w: number; h: number } | null>(null);

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const update = () => {
      const rect = svg.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) setContainerSize({ w: rect.width, h: rect.height });
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(svg);
    return () => observer.disconnect();
  }, []);

  const fullFit = useMemo<ViewBox>(
    () => ({ x: 0, y: 0, w: model.width, h: model.height }),
    [model.width, model.height],
  );
  const fit = useMemo<ViewBox>(() => {
    if (containerSize) {
      const fitScale = Math.min(containerSize.w / model.width, containerSize.h / model.height);
      if (fitScale < MIN_READABLE_SCALE) {
        // Anchor top-left (with toolbar headroom) so the session column and the
        // first node's title stay visible; pan right/down for the rest.
        const w = containerSize.w / MIN_READABLE_SCALE;
        const h = containerSize.h / MIN_READABLE_SCALE;
        return { x: 0, y: -CLAMPED_TOP_HEADROOM, w, h };
      }
    }
    return fullFit;
  }, [containerSize, model.width, model.height, fullFit]);
  const clamped = fit.w < model.width || fit.h < model.height;
  const view = viewBox ?? fit;

  useEffect(() => {
    if (!runtime.writer) return;
    const timer = window.setInterval(() => setNowMs(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [runtime.writer]);

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const handleWheel = (event: WheelEvent) => {
      event.preventDefault();
      setViewBox((current) => {
        const base = current ?? fit;
        const factor = event.deltaY > 0 ? 1.12 : 1 / 1.12;
        // Clamp monotonically relative to the current view: when the clamped
        // default is already tighter than the absolute zoom bound, zoom-in is a
        // no-op instead of a jump outward to the bound.
        const minW = Math.min(base.w, model.width / MAX_ZOOM);
        const maxW = Math.max(base.w, model.width / MIN_ZOOM);
        const clampedW = Math.min(maxW, Math.max(minW, base.w * factor));
        const rect = svg.getBoundingClientRect();
        const px = rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0.5;
        const py = rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.5;
        const clampedH = base.h * (clampedW / base.w);
        return {
          x: base.x + (base.w - clampedW) * px,
          y: base.y + (base.h - clampedH) * py,
          w: clampedW,
          h: clampedH,
        };
      });
    };
    svg.addEventListener("wheel", handleWheel, { passive: false });
    return () => svg.removeEventListener("wheel", handleWheel);
  }, [model.width, model.height, fit]);

  const clientDyToView = (clientDy: number): number => {
    const svg = svgRef.current;
    if (!svg) return 0;
    const rect = svg.getBoundingClientRect();
    return rect.height > 0 ? clientDy * (view.h / rect.height) : 0;
  };

  const handleNodePointerDown = (event: ReactPointerEvent<SVGGElement>, sessionId: string) => {
    const svg = svgRef.current;
    const node = model.nodes.find((candidate) => candidate.session?.id === sessionId);
    if (!svg || !node) return;
    event.stopPropagation();
    nodeDrag.current = {
      sessionId,
      pointerId: event.pointerId,
      startClientY: event.clientY,
      nodeCenterY: node.y + node.h / 2,
    };
    draggedRef.current = false;
    svg.setPointerCapture(event.pointerId);
  };

  const handlePointerDown = (event: ReactPointerEvent<SVGSVGElement>) => {
    if (event.target instanceof Element && event.target.closest(".gnode")) return;
    const svg = svgRef.current;
    if (!svg) return;
    panState.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      origin: viewBox ?? fit,
    };
    svg.setPointerCapture(event.pointerId);
    svg.classList.add("panning");
  };

  const handlePointerMove = (event: ReactPointerEvent<SVGSVGElement>) => {
    const drag = nodeDrag.current;
    if (drag && drag.pointerId === event.pointerId) {
      const dy = clientDyToView(event.clientY - drag.startClientY);
      if (Math.abs(dy) > DRAG_THRESHOLD) draggedRef.current = true;
      setDragState({ sessionId: drag.sessionId, dy });
      return;
    }
    const pan = panState.current;
    const svg = svgRef.current;
    if (!pan || !svg || pan.pointerId !== event.pointerId) return;
    const rect = svg.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return;
    const scaleX = pan.origin.w / rect.width;
    const scaleY = pan.origin.h / rect.height;
    setViewBox({
      x: pan.origin.x - (event.clientX - pan.startX) * scaleX,
      y: pan.origin.y - (event.clientY - pan.startY) * scaleY,
      w: pan.origin.w,
      h: pan.origin.h,
    });
  };

  const handlePointerUp = (event: ReactPointerEvent<SVGSVGElement>) => {
    const svg = svgRef.current;
    const drag = nodeDrag.current;
    if (drag && drag.pointerId === event.pointerId) {
      const dy = clientDyToView(event.clientY - drag.startClientY);
      const dragged = draggedRef.current;
      nodeDrag.current = null;
      setDragState(null);
      svg?.releasePointerCapture(event.pointerId);
      if (dragged && Math.abs(dy) > DRAG_THRESHOLD) {
        const droppedCenter = drag.nodeCenterY + dy;
        const candidates = model.nodes.filter(
          (node) => node.kind === "session" && node.session && node.session.id !== drag.sessionId,
        );
        let target: GraphNode | undefined;
        let best = Number.POSITIVE_INFINITY;
        for (const candidate of candidates) {
          const distance = Math.abs(candidate.y + candidate.h / 2 - droppedCenter);
          if (distance < best) {
            best = distance;
            target = candidate;
          }
        }
        if (target?.session && best < target.h) {
          onReorder(drag.sessionId, target.session.id);
        }
      } else {
        // A press without meaningful movement is a selection. Pointer capture on the
        // SVG swallows the synthesized click, so selection is handled here directly.
        onSelect(drag.sessionId);
      }
      return;
    }
    if (panState.current?.pointerId === event.pointerId) {
      panState.current = null;
      svg?.classList.remove("panning");
      svg?.releasePointerCapture(event.pointerId);
    }
  };

  const sessionNodes = model.nodes.filter((node) => node.kind === "session");
  const orchestratorNode = model.nodes.find((node) => node.kind === "orchestrator");
  const docNodes = model.nodes.filter((node) => node.kind === "doc");

  return (
    <svg
      ref={svgRef}
      className={`session-canvas health-${runtime.health}`}
      viewBox={`${view.x} ${view.y} ${view.w} ${view.h}`}
      preserveAspectRatio="xMidYMid meet"
      role="img"
      aria-label="Session graph: agent sessions wired through the single-writer orchestrator to the Rhino and Grasshopper documents"
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
      onDoubleClick={() =>
        setViewBox((current) => (current === null && clamped ? fullFit : null))
      }
    >
      <defs>
        <pattern id="gptino-grid" width={22} height={22} patternUnits="userSpaceOnUse">
          <circle cx={1.4} cy={1.4} r={0.8} className="grid-dot" />
        </pattern>
        <linearGradient id="gptino-edge-fade" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="#0c0e0f" stopOpacity="0" />
          <stop offset="1" stopColor="#0c0e0f" stopOpacity="0.9" />
        </linearGradient>
      </defs>
      <rect
        className="canvas-backdrop"
        x={view.x - view.w}
        y={view.y - view.h}
        width={view.w * 3}
        height={view.h * 3}
        fill="url(#gptino-grid)"
      />
      {model.edges.map((edge) => (
        <Wire key={edge.id} edge={edge} />
      ))}
      {sessionNodes.map((node) => (
        <SessionNode
          key={node.id}
          node={node}
          selected={node.session?.id === selectedId}
          dragOffset={dragState && dragState.sessionId === node.session?.id ? dragState.dy : 0}
          onSelect={onSelect}
          onNodePointerDown={handleNodePointerDown}
          onPauseToggle={onPauseToggle}
        />
      ))}
      {sessionNodes.length === 0 ? (
        <text className="canvas-empty" x={100} y={model.height / 2}>
          No sessions yet — create one with the + Session button.
        </text>
      ) : null}
      {orchestratorNode ? <OrchestratorNode node={orchestratorNode} nowMs={nowMs} /> : null}
      {docNodes.map((node) => (
        <DocNode key={node.id} node={node} />
      ))}
      {view.x + view.w < model.width - 1 ? (
        <g pointerEvents="none">
          <title>More to the right — drag to pan, double-click to fit everything.</title>
          <rect
            x={view.x + view.w - 34}
            y={view.y}
            width={34}
            height={view.h}
            fill="url(#gptino-edge-fade)"
          />
        </g>
      ) : null}
    </svg>
  );
}
