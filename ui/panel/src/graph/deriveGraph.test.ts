import { describe, expect, it } from "vitest";
import { createDemoRuntimeState } from "../api/mock";
import { deriveGraph } from "./deriveGraph";

const edge = (model: ReturnType<typeof deriveGraph>, id: string) => {
  const found = model.edges.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`Missing edge ${id}`);
  return found;
};

const node = (model: ReturnType<typeof deriveGraph>, id: string) => {
  const found = model.nodes.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`Missing node ${id}`);
  return found;
};

describe("deriveGraph", () => {
  it("builds one node per session plus the orchestrator and both document nodes", () => {
    const state = createDemoRuntimeState();
    const model = deriveGraph(state);

    const sessionNodes = model.nodes.filter((candidate) => candidate.kind === "session");
    expect(sessionNodes.map((candidate) => candidate.id)).toEqual(
      state.sessions.map((session) => `session:${session.id}`),
    );
    expect(sessionNodes.map((candidate) => candidate.rank)).toEqual([1, 2, 3, 4]);
    expect(node(model, "orchestrator").kind).toBe("orchestrator");
    expect(node(model, "doc:rhino").docTarget).toBe("rhino");
    expect(node(model, "doc:gh").docTarget).toBe("grasshopper");
    expect(node(model, "session:facade").sublabel).toBe("Submitting: Rebuild panel boundaries");
  });

  it("marks only the writer session wire as active and animated", () => {
    const model = deriveGraph(createDemoRuntimeState());

    expect(edge(model, "wire:facade").kind).toBe("active");
    expect(edge(model, "wire:facade").animated).toBe(true);
    const otherActive = model.edges.filter(
      (candidate) => candidate.kind === "active" && candidate.id !== "wire:facade",
    );
    expect(otherActive).toEqual([]);
  });

  it("labels queued wires with their pending-queue position", () => {
    const model = deriveGraph(createDemoRuntimeState());

    const wires = edge(model, "wire:wires");
    expect(wires.kind).toBe("queued");
    expect(wires.label).toBe("#1");
  });

  it("derives blocked, paused, and idle wire kinds from session state", () => {
    const state = createDemoRuntimeState();
    const model = deriveGraph(state);
    expect(edge(model, "wire:option-b").kind).toBe("blocked");
    expect(edge(model, "wire:layers").kind).toBe("paused");

    const idleState = createDemoRuntimeState();
    const layers = idleState.sessions.find((session) => session.id === "layers");
    if (!layers) throw new Error("Missing layers session");
    layers.paused = false;
    layers.status = "idle";
    expect(edge(deriveGraph(idleState), "wire:layers").kind).toBe("idle");
  });

  it("renders pairwise conflicts as edges and single-session conflicts as node warnings", () => {
    const model = deriveGraph(createDemoRuntimeState());

    const conflict = edge(model, "conflict:conflict-8");
    expect(conflict.kind).toBe("conflict");
    expect([conflict.from, conflict.to]).toEqual(["session:facade", "session:wires"]);
    expect(model.edges.some((candidate) => candidate.id === "conflict:conflict-7")).toBe(false);
    expect(node(model, "session:option-b").warning).toContain("Manual geometry drift");
  });

  it("projects broker state onto the orchestrator node", () => {
    const state = createDemoRuntimeState();
    state.paused = true;
    const orchestrator = node(deriveGraph(state), "orchestrator").orchestrator;

    expect(orchestrator?.paused).toBe(true);
    expect(orchestrator?.live).toBe(true);
    expect(orchestrator?.queueDepth).toBe(3);
    expect(orchestrator?.revision).toBe(state.revision);
    expect(orchestrator?.writerSessionTitle).toBe("Facade rationalization");
  });

  it("animates document wires by writer target, both without target info, none without a writer", () => {
    const targeted = deriveGraph(createDemoRuntimeState());
    expect(edge(targeted, "commit:grasshopper").animated).toBe(true);
    expect(edge(targeted, "commit:rhino").animated).toBe(false);

    const untargeted = createDemoRuntimeState();
    untargeted.queue = untargeted.queue.map(({ target: _target, ...item }) => item);
    const untargetedModel = deriveGraph(untargeted);
    expect(edge(untargetedModel, "commit:grasshopper").animated).toBe(true);
    expect(edge(untargetedModel, "commit:rhino").animated).toBe(true);

    const idle = createDemoRuntimeState();
    idle.writer = undefined;
    const idleModel = deriveGraph(idle);
    expect(edge(idleModel, "commit:grasshopper").animated).toBe(false);
    expect(edge(idleModel, "commit:rhino").animated).toBe(false);
  });

  it("surfaces the pushed Rhino selection on the Rhino doc node", () => {
    const model = deriveGraph(createDemoRuntimeState());
    expect(node(model, "doc:rhino").detail).toBe("2 selected · Facade::Panels");
    expect(node(model, "doc:gh").detail).toBeUndefined();

    const cleared = createDemoRuntimeState();
    cleared.currentSelection = null;
    expect(node(deriveGraph(cleared), "doc:rhino").detail).toBeUndefined();
  });

  it("is deterministic and keeps every node inside the canvas bounds", () => {
    const state = createDemoRuntimeState();
    const first = deriveGraph(state);
    const second = deriveGraph(state);
    expect(second).toEqual(first);

    for (const candidate of first.nodes) {
      expect(candidate.x).toBeGreaterThanOrEqual(0);
      expect(candidate.y).toBeGreaterThanOrEqual(0);
      expect(candidate.x + candidate.w).toBeLessThanOrEqual(first.width);
      expect(candidate.y + candidate.h).toBeLessThanOrEqual(first.height);
    }

    const sessionNodes = first.nodes.filter((candidate) => candidate.kind === "session");
    const sortedByY = [...sessionNodes].sort((a, b) => a.y - b.y);
    expect(sortedByY).toEqual(sessionNodes);
  });

  it("tolerates empty runtimes and writers that reference unknown sessions", () => {
    const empty = createDemoRuntimeState();
    empty.sessions = [];
    empty.queue = [];
    empty.conflicts = [];
    empty.writer = undefined;
    const emptyModel = deriveGraph(empty);
    expect(emptyModel.nodes.map((candidate) => candidate.id)).toEqual([
      "orchestrator",
      "doc:rhino",
      "doc:gh",
    ]);

    const stale = createDemoRuntimeState();
    stale.writer = { ...stale.writer!, sessionId: "gone" };
    stale.queue = [];
    const staleModel = deriveGraph(stale);
    expect(staleModel.edges.filter((candidate) => candidate.kind === "active")).toEqual([]);
    expect(node(staleModel, "orchestrator").orchestrator?.live).toBe(true);
  });
});
