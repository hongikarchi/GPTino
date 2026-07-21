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
  onTerminal(id: string): void;
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

const truncate = (value: string, max: number) =>
  value.length > max ? `${value.slice(0, max - 1)}…` : value;

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
  onTerminal,
  suppressClick,
}: {
  node: GraphNode;
  selected: boolean;
  dragOffset: number;
  onSelect(id: string): void;
  onNodePointerDown(event: ReactPointerEvent<SVGGElement>, sessionId: string): void;
  onPauseToggle(id: string, paused: boolean): void;
  onTerminal(id: string): void;
  suppressClick(): boolean;
}) {
  const session = node.session;
  if (!session) return null;

  const handleKeyDown = (event: ReactKeyboardEvent<SVGGElement>) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(session.id);
    }
  };

  const handleClick = () => {
    if (!suppressClick()) onSelect(session.id);
  };

  return (
    <g
      className={`gnode gnode-session status-${session.status}${selected ? " selected" : ""}${session.paused ? " paused" : ""}${node.warning ? " warning" : ""}${dragOffset !== 0 ? " dragging" : ""}`}
      transform={`translate(${node.x}, ${node.y + dragOffset})`}
      role="button"
      tabIndex={0}
      aria-label={`Session ${session.title}`}
      onClick={handleClick}
      onKeyDown={handleKeyDown}
      onPointerDown={(event) => onNodePointerDown(event, session.id)}
    >
      <title>{sessionTooltip(node)}</title>
      <rect className="gnode-box" width={node.w} height={node.h} rx={8} />
      <text className="gnode-rank" x={10} y={17}>{node.rank}</text>
      <text className="gnode-title" x={24} y={17}>{truncate(session.title, 21)}</text>
      {session.terminalOpen ? (
        <text className="gnode-terminal" x={node.w - 26} y={17}>{"❯_"}</text>
      ) : null}
      {node.warning ? (
        <text className="gnode-warning" x={node.w - 12} y={17}>!</text>
      ) : null}
      <circle className="gnode-status-dot" cx={13} cy={33} r={3} />
      <text className="gnode-status" x={22} y={36}>{session.status}</text>
      <text className="gnode-chips" x={10} y={53}>
        {truncate(`${session.pinnedModel ?? session.modelProfile} · ${session.mode}`, 26)}
      </text>
      {node.sublabel ? (
        <text className="gnode-sub" x={10} y={65}>{truncate(node.sublabel, 30)}</text>
      ) : null}
      {selected ? (
        <g className="gnode-actions">
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
            <circle cx={node.w - 40} cy={node.h - 12} r={8} />
            {session.paused ? (
              <path className="gnode-action-glyph" d={`M ${node.w - 43} ${node.h - 16} l 6 4 l -6 4 Z`} />
            ) : (
              <>
                <line className="gnode-action-glyph" x1={node.w - 42.5} y1={node.h - 15} x2={node.w - 42.5} y2={node.h - 9} />
                <line className="gnode-action-glyph" x1={node.w - 37.5} y1={node.h - 15} x2={node.w - 37.5} y2={node.h - 9} />
              </>
            )}
          </g>
          <g
            className="gnode-action"
            role="button"
            aria-label="Open session terminal"
            onClick={(event) => {
              event.stopPropagation();
              onTerminal(session.id);
            }}
            onPointerDown={(event) => event.stopPropagation()}
          >
            <title>Open terminal window</title>
            <circle cx={node.w - 18} cy={node.h - 12} r={8} />
            <text className="gnode-action-text" x={node.w - 18} y={node.h - 9}>{"❯_"}</text>
          </g>
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
      <circle className="gnode-lamp" cx={16} cy={19} r={4} />
      <text className="gnode-heading" x={28} y={23}>ORCHESTRATOR</text>
      <text className="gnode-subheading" x={28} y={36}>single writer</text>
      {info.paused ? (
        <text className="gnode-writer" x={14} y={62}>Paused</text>
      ) : info.live ? (
        <>
          <text className="gnode-writer" x={14} y={60}>
            {truncate(info.writerSessionTitle ?? "Executing", 22)}
          </text>
          <text className="gnode-phase" x={14} y={74}>
            {truncate(`${info.writerPhase ?? "Executing"}${elapsed ? ` · ${elapsed}` : ""}`, 26)}
          </text>
        </>
      ) : (
        <text className="gnode-writer idle" x={14} y={62}>Idle — waiting for jobs</text>
      )}
      <text className="gnode-footer" x={14} y={node.h - 12}>
        {`QUEUE ${info.queueDepth} · r${info.revision}`}
      </text>
    </g>
  );
}

function DocNode({ node }: { node: GraphNode }) {
  return (
    <g className={`gnode gnode-doc doc-${node.docTarget}`} transform={`translate(${node.x}, ${node.y})`}>
      <title>{`${node.label} · ${node.sublabel ?? ""}`}</title>
      <rect className="gnode-box" width={node.w} height={node.h} rx={8} />
      <rect className="gnode-doc-mark" x={10} y={node.h / 2 - 13} width={26} height={26} rx={6} />
      <text className="gnode-doc-glyph" x={23} y={node.h / 2 + 4}>
        {node.docTarget === "rhino" ? "R" : "GH"}
      </text>
      <text className="gnode-title" x={46} y={node.h / 2 - (node.detail ? 10 : 3)}>{node.label}</text>
      <text className="gnode-sub" x={46} y={node.h / 2 + (node.detail ? 3 : 12)}>
        {truncate(node.sublabel ?? "", 15)}
      </text>
      {node.detail ? (
        <text className="gnode-detail" x={46} y={node.h / 2 + 16}>{truncate(node.detail, 18)}</text>
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
  onTerminal,
}: SessionCanvasProps) {
  const model = useMemo(() => deriveGraph(runtime), [runtime]);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const [viewBox, setViewBox] = useState<ViewBox | null>(null);
  const panState = useRef<{ pointerId: number; startX: number; startY: number; origin: ViewBox } | null>(null);
  const nodeDrag = useRef<NodeDrag | null>(null);
  const [dragState, setDragState] = useState<{ sessionId: string; dy: number } | null>(null);
  const suppressClickRef = useRef(false);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const fit: ViewBox = { x: 0, y: 0, w: model.width, h: model.height };
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
        const base = current ?? { x: 0, y: 0, w: model.width, h: model.height };
        const factor = event.deltaY > 0 ? 1.12 : 1 / 1.12;
        const zoom = model.width / (base.w * factor);
        const clampedW =
          zoom > MAX_ZOOM ? model.width / MAX_ZOOM : zoom < MIN_ZOOM ? model.width / MIN_ZOOM : base.w * factor;
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
  }, [model.width, model.height]);

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
    suppressClickRef.current = false;
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
      if (Math.abs(dy) > DRAG_THRESHOLD) suppressClickRef.current = true;
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
      nodeDrag.current = null;
      setDragState(null);
      svg?.releasePointerCapture(event.pointerId);
      if (Math.abs(dy) > DRAG_THRESHOLD) {
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
      onDoubleClick={() => setViewBox(null)}
    >
      <defs>
        <pattern id="gptino-grid" width={22} height={22} patternUnits="userSpaceOnUse">
          <circle cx={1.4} cy={1.4} r={0.8} className="grid-dot" />
        </pattern>
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
          onTerminal={onTerminal}
          suppressClick={() => {
            const suppressed = suppressClickRef.current;
            suppressClickRef.current = false;
            return suppressed;
          }}
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
    </svg>
  );
}
