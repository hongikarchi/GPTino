import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { createDemoRuntimeState, createMockApiClient } from "./api/mock";
import { HALT_RESUME_FAILED_MESSAGE, runHaltResume, truncateHaltMessage } from "./components/ChatPane";
import { deriveGraph } from "./graph/deriveGraph";

// The mock client's delay() runs on window timers; vitest runs in a node env,
// so give it just the timer surface it needs.
beforeAll(() => {
  vi.stubGlobal("window", {
    setTimeout: globalThis.setTimeout.bind(globalThis),
    clearTimeout: globalThis.clearTimeout.bind(globalThis),
  });
});
afterAll(() => {
  vi.unstubAllGlobals();
});

describe("halted session projection", () => {
  it("ships a demo fixture carrying the full halt contract shape", () => {
    const state = createDemoRuntimeState();
    const halted = state.sessions.find((session) => session.halt != null);
    expect(halted).toBeDefined();
    expect(halted!.halt!.jobId).toMatch(/^[0-9a-f-]{36}$/);
    expect(halted!.halt!.message.length).toBeGreaterThan(0);
    expect(Number.isNaN(Date.parse(halted!.halt!.at))).toBe(false);
  });

  it("marks the halted session's graph node with a 복구 필요 warning", () => {
    const state = createDemoRuntimeState();
    const halted = state.sessions.find((session) => session.halt != null)!;
    const node = deriveGraph(state).nodes.find((candidate) => candidate.id === `session:${halted.id}`);
    expect(node?.warning).toContain("복구 필요로 정지됨");
  });

  it("resume clears the halt and is idempotent on a non-halted session", async () => {
    const client = createMockApiClient();
    const before = await client.getRuntime();
    const halted = before.sessions.find((session) => session.halt != null)!;

    await client.resumeHaltedSession(halted.id);
    const after = await client.getRuntime();
    expect(after.sessions.find((session) => session.id === halted.id)!.halt).toBeNull();

    // Contract: resuming a session that is not halted is also a success (204), not an error.
    await expect(client.resumeHaltedSession(halted.id)).resolves.toBeUndefined();
  });
});

// The resume flow is NOT optimistic: the halt only clears after the server's 204 (via refetch),
// so the banner instance survives the whole request — its busy label stays visible and, on
// failure, its inline error state is not lost to a rollback remount.
describe("runHaltResume", () => {
  it("reports the inline failure message when the POST fails, after clearing the previous one", async () => {
    const transitions: Array<string | null> = [];
    await runHaltResume(false, () => Promise.resolve(false), (message) => transitions.push(message));
    // First the stale error is cleared (retry starts clean), then the failure lands.
    expect(transitions).toEqual([null, HALT_RESUME_FAILED_MESSAGE]);
  });

  it("leaves no failure message on success", async () => {
    const transitions: Array<string | null> = [];
    await runHaltResume(false, () => Promise.resolve(true), (message) => transitions.push(message));
    expect(transitions).toEqual([null]);
  });

  it("never double-fires while a resume is already in flight", async () => {
    let calls = 0;
    await runHaltResume(
      true,
      () => {
        calls += 1;
        return Promise.resolve(true);
      },
      () => undefined,
    );
    expect(calls).toBe(0);
  });
});

describe("truncateHaltMessage", () => {
  it("passes short messages through untouched", () => {
    expect(truncateHaltMessage("짧은 메시지", 160)).toBe("짧은 메시지");
  });

  it("truncates long messages to the limit with an ellipsis", () => {
    const long = "가".repeat(400);
    const truncated = truncateHaltMessage(long, 160);
    expect(truncated.length).toBeLessThanOrEqual(160);
    expect(truncated.endsWith("…")).toBe(true);
  });
});
