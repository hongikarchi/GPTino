import { describe, expect, it } from "vitest";
import { detectCompletions } from "./useSessionCompletion";
import type { GptinoSession, RuntimeState, SessionStatus } from "../types";

const session = (id: string, status: SessionStatus, over: Partial<GptinoSession> = {}): GptinoSession => ({
  id,
  title: `Session ${id}`,
  status,
  mode: "auto",
  modelProfile: "xhigh",
  paused: false,
  messages: [],
  ...over,
});

const runtime = (
  sessions: GptinoSession[],
  health: RuntimeState["health"] = "connected",
  over: Partial<RuntimeState> = {},
): RuntimeState => ({
  projectId: "p",
  projectName: "P",
  rhinoFile: "a.3dm",
  grasshopperFile: "a.gh",
  health,
  revision: 1,
  orderVersion: 1,
  paused: false,
  sessions,
  queue: [],
  conflicts: [],
  lastUpdatedAt: "now",
  ...over,
});

describe("detectCompletions", () => {
  it("seeds on first sight without firing", () => {
    const result = detectCompletions(new Map(), null, runtime([session("a", "idle"), session("b", "blocked")]));
    expect(result.events).toEqual([]);
    expect(result.nextStatus.get("a")).toBe("idle");
    expect(result.nextStatus.get("b")).toBe("blocked");
  });

  it("fires success on working -> idle", () => {
    const prev = new Map<string, SessionStatus>([["a", "working"]]);
    const result = detectCompletions(prev, "connected", runtime([session("a", "idle")]));
    expect(result.events).toEqual([{ sessionId: "a", title: "Session a", kind: "success", detail: "" }]);
  });

  it("fires attention on queued -> blocked and prefers the matching conflict detail", () => {
    const prev = new Map<string, SessionStatus>([["a", "queued"]]);
    const state = runtime([session("a", "blocked")], "connected", {
      conflicts: [{ id: "c1", title: "Drift", detail: "Object 7F2A changed after snapshot.", sessionIds: ["a"] }],
    });
    const result = detectCompletions(prev, "connected", state);
    expect(result.events).toEqual([
      { sessionId: "a", title: "Session a", kind: "attention", detail: "Object 7F2A changed after snapshot." },
    ]);
  });

  it("uses the last assistant message as the success detail", () => {
    const prev = new Map<string, SessionStatus>([["a", "working"]]);
    const withMsg = session("a", "idle", {
      messages: [
        { id: "1", role: "user", content: "do it", createdAt: "t0" },
        { id: "2", role: "assistant", content: "All done and verified.", createdAt: "t1" },
      ],
    });
    const result = detectCompletions(prev, "connected", runtime([withMsg]));
    expect(result.events[0].detail).toBe("All done and verified.");
  });

  it("does not fire when the previous status was not active (paused -> idle)", () => {
    const prev = new Map<string, SessionStatus>([["a", "paused"]]);
    const result = detectCompletions(prev, "connected", runtime([session("a", "idle")]));
    expect(result.events).toEqual([]);
  });

  it("does not fire across a reconnect (prev health was disconnected)", () => {
    const prev = new Map<string, SessionStatus>([["a", "working"]]);
    const result = detectCompletions(prev, "disconnected", runtime([session("a", "idle")], "connected"));
    expect(result.events).toEqual([]);
    // ...but it reseeds the status so the next real edge can fire.
    expect(result.nextStatus.get("a")).toBe("idle");
  });

  it("does not fire when the current snapshot is not connected", () => {
    const prev = new Map<string, SessionStatus>([["a", "working"]]);
    const result = detectCompletions(prev, "connected", runtime([session("a", "idle")], "degraded"));
    expect(result.events).toEqual([]);
  });

  it("prunes deleted sessions from nextStatus", () => {
    const prev = new Map<string, SessionStatus>([["a", "working"], ["gone", "working"]]);
    const result = detectCompletions(prev, "connected", runtime([session("a", "working")]));
    expect(result.nextStatus.has("gone")).toBe(false);
  });
});
