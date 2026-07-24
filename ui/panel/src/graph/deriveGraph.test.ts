import { describe, expect, it } from "vitest";
import { createDemoRuntimeState } from "../api/mock";
import { deriveGraph } from "./deriveGraph";

// The demo state's two GH docKeys, pinned deliberately (api/mock.ts).
const DOC_FACADE = "4f2a91c7d05e83b6";
const DOC_ATRIUM = "b81d4e9a2c7f5063";

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
  it("builds one node per session plus the orchestrator, the Rhino doc, and one node per GH doc", () => {
    const state = createDemoRuntimeState();
    const model = deriveGraph(state);

    const sessionNodes = model.nodes.filter((candidate) => candidate.kind === "session");
    expect(sessionNodes.map((candidate) => candidate.id)).toEqual(
      state.sessions.map((session) => `session:${session.id}`),
    );
    expect(sessionNodes.map((candidate) => candidate.rank)).toEqual([1, 2, 3, 4]);
    expect(node(model, "orchestrator").kind).toBe("orchestrator");
    expect(node(model, "doc:rhino").docTarget).toBe("rhino");
    expect(node(model, `doc:gh:${DOC_FACADE}`).docTarget).toBe("grasshopper");
    expect(node(model, `doc:gh:${DOC_FACADE}`).docId).toBe(DOC_FACADE);
    expect(node(model, `doc:gh:${DOC_FACADE}`).sublabel).toBe("Facade_Paneling.gh");
    expect(node(model, `doc:gh:${DOC_ATRIUM}`).docTarget).toBe("grasshopper");
    expect(node(model, `doc:gh:${DOC_ATRIUM}`).sublabel).toBe("Atrium_Options.gh");
    expect(model.nodes.some((candidate) => candidate.id === "doc:gh")).toBe(false);
    expect(node(model, "session:facade").sublabel).toBe("Submitting: Rebuild panel boundaries");
  });

  it("renders the legacy single GH node when grasshopperDocs is null or absent", () => {
    for (const legacy of [
      (() => {
        const state = createDemoRuntimeState();
        state.grasshopperDocs = null;
        return state;
      })(),
      (() => {
        const state = createDemoRuntimeState();
        delete state.grasshopperDocs;
        return state;
      })(),
    ]) {
      const model = deriveGraph(legacy);
      const ghNode = node(model, "doc:gh");
      expect(ghNode.docTarget).toBe("grasshopper");
      expect(ghNode.docId).toBeUndefined();
      expect(ghNode.sublabel).toBe("Facade_Paneling.gh");
      expect(model.nodes.filter((candidate) => candidate.kind === "doc")).toHaveLength(2);
      // Legacy selection badge stays on the single GH node.
      expect(ghNode.detail).toBe("3 selected · Panel Grid…");
      // Legacy commit wires keep their historical ids and port positions.
      const orchestrator = node(model, "orchestrator");
      expect(edge(model, "commit:rhino").y1).toBe(orchestrator.y + orchestrator.h / 3);
      expect(edge(model, "commit:grasshopper").y1).toBe(orchestrator.y + (orchestrator.h * 2) / 3);
      // The writer job carries targetDocId, but the legacy node has no docId —
      // the coarse target string alone decides the animation.
      expect(edge(model, "commit:grasshopper").animated).toBe(true);
      expect(edge(model, "commit:rhino").animated).toBe(false);
    }
  });

  it("distributes one orchestrator output port per document, mirroring the session input ports", () => {
    const model = deriveGraph(createDemoRuntimeState());
    const orchestrator = node(model, "orchestrator");
    const commits = [
      edge(model, "commit:rhino"),
      edge(model, `commit:gh:${DOC_FACADE}`),
      edge(model, `commit:gh:${DOC_ATRIUM}`),
    ];
    commits.forEach((commit, index) => {
      expect(commit.x1).toBe(orchestrator.x + orchestrator.w);
      expect(commit.y1).toBe(orchestrator.y + (orchestrator.h * (index + 1)) / (commits.length + 1));
      const target = node(model, commit.to);
      expect(commit.x2).toBe(target.x);
      expect(commit.y2).toBe(target.y + target.h / 2);
    });
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

  it("animates only the targeted GH doc wire when the writer job carries a targetDocId", () => {
    const targeted = deriveGraph(createDemoRuntimeState());
    expect(edge(targeted, `commit:gh:${DOC_FACADE}`).animated).toBe(true);
    expect(edge(targeted, `commit:gh:${DOC_ATRIUM}`).animated).toBe(false);
    expect(edge(targeted, "commit:rhino").animated).toBe(false);

    // Retargeting the writer job moves the animation to the other doc node.
    const retargeted = createDemoRuntimeState();
    retargeted.queue = retargeted.queue.map((item) =>
      item.id === "job-184" ? { ...item, targetDocId: DOC_ATRIUM } : item,
    );
    const retargetedModel = deriveGraph(retargeted);
    expect(edge(retargetedModel, `commit:gh:${DOC_ATRIUM}`).animated).toBe(true);
    expect(edge(retargetedModel, `commit:gh:${DOC_FACADE}`).animated).toBe(false);
  });

  it("falls back to animating every GH wire when the writer job has no targetDocId", () => {
    // Coarse target "grasshopper" without doc attribution → all GH wires animate.
    const untraced = createDemoRuntimeState();
    untraced.queue = untraced.queue.map((item) =>
      item.id === "job-184" ? { ...item, targetDocId: null } : item,
    );
    const untracedModel = deriveGraph(untraced);
    expect(edge(untracedModel, `commit:gh:${DOC_FACADE}`).animated).toBe(true);
    expect(edge(untracedModel, `commit:gh:${DOC_ATRIUM}`).animated).toBe(true);
    expect(edge(untracedModel, "commit:rhino").animated).toBe(false);

    // No target info at all → every document wire animates (today's semantics).
    const untargeted = createDemoRuntimeState();
    untargeted.queue = untargeted.queue.map(({ target: _target, targetDocId: _targetDocId, ...item }) => item);
    const untargetedModel = deriveGraph(untargeted);
    expect(edge(untargetedModel, `commit:gh:${DOC_FACADE}`).animated).toBe(true);
    expect(edge(untargetedModel, `commit:gh:${DOC_ATRIUM}`).animated).toBe(true);
    expect(edge(untargetedModel, "commit:rhino").animated).toBe(true);

    const idle = createDemoRuntimeState();
    idle.writer = undefined;
    const idleModel = deriveGraph(idle);
    expect(idleModel.edges.filter((candidate) => candidate.kind === "commit" && candidate.animated)).toEqual([]);

    // The live server serializes an idle writer as an explicit null, not undefined.
    const nullWriter = createDemoRuntimeState();
    nullWriter.writer = null;
    const nullModel = deriveGraph(nullWriter);
    expect(node(nullModel, "orchestrator").orchestrator?.live).toBe(false);
    expect(nullModel.edges.filter((candidate) => candidate.kind === "commit" && candidate.animated)).toEqual([]);
  });

  it("attaches the GH selection badge to the doc the selection was observed in", () => {
    const model = deriveGraph(createDemoRuntimeState());
    expect(node(model, "doc:rhino").detail).toBe("2 selected · layer Facade::Panels");
    expect(node(model, `doc:gh:${DOC_FACADE}`).detail).toBe("3 selected · Panel Grid…");
    expect(node(model, `doc:gh:${DOC_FACADE}`).tooltip).toContain("Panel Grid, Offset, Area");
    expect(node(model, `doc:gh:${DOC_ATRIUM}`).detail).toBeUndefined();
    expect(node(model, `doc:gh:${DOC_ATRIUM}`).tooltip).toBeUndefined();

    // Attribution follows the selection's docId.
    const moved = createDemoRuntimeState();
    moved.currentSelection!.docId = DOC_ATRIUM;
    const movedModel = deriveGraph(moved);
    expect(node(movedModel, `doc:gh:${DOC_ATRIUM}`).detail).toBe("3 selected · Panel Grid…");
    expect(node(movedModel, `doc:gh:${DOC_FACADE}`).detail).toBeUndefined();

    // Unattributed selection with several docs is shown on none rather than wrongly.
    const unattributed = createDemoRuntimeState();
    unattributed.currentSelection!.docId = null;
    const unattributedModel = deriveGraph(unattributed);
    expect(node(unattributedModel, `doc:gh:${DOC_FACADE}`).detail).toBeUndefined();
    expect(node(unattributedModel, `doc:gh:${DOC_ATRIUM}`).detail).toBeUndefined();

    // Unattributed selection with a single registered doc lands on that doc.
    const single = createDemoRuntimeState();
    single.grasshopperDocs = [single.grasshopperDocs![0]];
    single.currentSelection!.docId = null;
    expect(node(deriveGraph(single), `doc:gh:${DOC_FACADE}`).detail).toBe("3 selected · Panel Grid…");

    const cleared = createDemoRuntimeState();
    cleared.currentSelection = null;
    expect(node(deriveGraph(cleared), "doc:rhino").detail).toBeUndefined();
    expect(node(deriveGraph(cleared), `doc:gh:${DOC_FACADE}`).detail).toBeUndefined();

    const rhinoOnly = createDemoRuntimeState();
    delete rhinoOnly.currentSelection!.grasshopperObjectCount;
    delete rhinoOnly.currentSelection!.grasshopperObjects;
    expect(node(deriveGraph(rhinoOnly), `doc:gh:${DOC_FACADE}`).detail).toBeUndefined();
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

    const docNodes = first.nodes.filter((candidate) => candidate.kind === "doc");
    const docsSortedByY = [...docNodes].sort((a, b) => a.y - b.y);
    expect(docsSortedByY).toEqual(docNodes);
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
      `doc:gh:${DOC_FACADE}`,
      `doc:gh:${DOC_ATRIUM}`,
    ]);

    const stale = createDemoRuntimeState();
    stale.writer = { ...stale.writer!, sessionId: "gone" };
    stale.queue = [];
    const staleModel = deriveGraph(stale);
    expect(staleModel.edges.filter((candidate) => candidate.kind === "active")).toEqual([]);
    expect(node(staleModel, "orchestrator").orchestrator?.live).toBe(true);
  });
});
