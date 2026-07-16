import type { GptinoApiClient } from "./client";
import type {
  ChatMessage,
  MessageRequest,
  ModelProfile,
  RuntimeState,
  SessionMode,
  SessionOrderRequest,
} from "../types";

const now = new Date();
const minutesAgo = (minutes: number) => new Date(now.getTime() - minutes * 60_000).toISOString();
const delay = (ms = 120) => new Promise((resolve) => window.setTimeout(resolve, ms));

const initialState: RuntimeState = {
  projectId: "prj-tower-a31f924c",
  projectName: "Tower study / Option A",
  rhinoFile: "Tower_Study_A.3dm",
  grasshopperFile: "Facade_Paneling.gh",
  health: "connected",
  healthDetail: "Rhino 8 · Grasshopper attached",
  revision: 42,
  gitRevision: 17,
  orderVersion: 8,
  paused: false,
  writer: {
    sessionId: "facade",
    jobId: "job-184",
    label: "Rebuild panel boundaries",
    phase: "Verifying geometry",
    startedAt: minutesAgo(2),
    progress: 72,
  },
  sessions: [
    {
      id: "facade",
      title: "Facade rationalization",
      summary: "Panel grid and boundary cleanup",
      status: "verifying",
      mode: "auto",
      modelProfile: "deep",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "xhigh",
      paused: false,
      terminalOpen: true,
      unread: 1,
      job: {
        id: "job-184",
        title: "Rebuild panel boundaries",
        phase: "Verifying geometry",
        progress: 72,
        baseRevision: 41,
      },
      messages: [
        {
          id: "m-f-1",
          role: "user",
          content: "Keep the existing tower silhouette, but rationalize the facade into four repeatable panel families.",
          createdAt: minutesAgo(18),
        },
        {
          id: "m-f-2",
          role: "assistant",
          content: "I found 126 unique panels driven mainly by edge tolerance. I can reduce them to four families without moving the primary grid. I am testing the remap in a shadow definition now.",
          createdAt: minutesAgo(13),
        },
        {
          id: "m-f-3",
          role: "assistant",
          content: "The shadow solve passed. GPTino is applying the typed geometry changes and checking panel areas, boundary closure, and wire topology before committing revision 18.",
          createdAt: minutesAgo(2),
        },
      ],
    },
    {
      id: "wires",
      title: "Wire cleanup",
      summary: "Reconnect three staged sockets",
      status: "queued",
      mode: "auto",
      modelProfile: "fast",
      effectiveModel: "gpt-5.6-terra",
      reasoning: "low",
      paused: false,
      messages: [
        {
          id: "m-w-1",
          role: "user",
          content: "Connect the three numbered outputs to the matching inputs and keep the existing data-tree paths.",
          createdAt: minutesAgo(9),
        },
        {
          id: "m-w-2",
          role: "assistant",
          content: "Targets and socket types are unambiguous. The ChangeSet is ready and waiting for the current writer.",
          createdAt: minutesAgo(8),
        },
      ],
      job: {
        id: "job-185",
        title: "Connect staged sockets",
        phase: "Waiting for writer",
        baseRevision: 42,
      },
    },
    {
      id: "layers",
      title: "Rhino layers",
      summary: "Normalize generated layer names",
      status: "paused",
      mode: "plan",
      modelProfile: "standard",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "medium",
      paused: true,
      messages: [
        {
          id: "m-l-1",
          role: "user",
          content: "Review the generated Rhino layers and propose a cleaner naming system. Do not apply it yet.",
          createdAt: minutesAgo(31),
        },
        {
          id: "m-l-2",
          role: "assistant",
          content: "I drafted a six-layer convention and mapped every managed object. This session is paused in Plan mode, so no live ChangeSet has been submitted.",
          createdAt: minutesAgo(25),
        },
      ],
    },
    {
      id: "option-b",
      title: "Option B",
      summary: "Alternative atrium geometry",
      status: "blocked",
      mode: "auto",
      modelProfile: "deep",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "xhigh",
      paused: false,
      messages: [
        {
          id: "m-b-1",
          role: "user",
          content: "Test a second atrium option with the same usable floor area and a softer corner transition.",
          createdAt: minutesAgo(44),
        },
        {
          id: "m-b-2",
          role: "assistant",
          content: "The existing atrium boundary was edited manually after snapshot r39. I paused this task because the requested geometry overlaps that drift. Reinitialize the baseline or resolve the manual edit to continue.",
          createdAt: minutesAgo(35),
        },
      ],
    },
  ],
  queue: [
    {
      id: "job-184",
      sessionId: "facade",
      title: "Rebuild panel boundaries",
      state: "verifying",
      resource: "GH · 48 components",
    },
    {
      id: "job-185",
      sessionId: "wires",
      title: "Connect staged sockets",
      state: "ready",
      resource: "GH · 3 wires",
    },
    {
      id: "job-187",
      sessionId: "option-b",
      title: "Regenerate atrium boundary",
      state: "waiting",
      waitingFor: "Manual drift resolution",
      resource: "Rhino · object 7F2A",
    },
  ],
  conflicts: [
    {
      id: "conflict-7",
      title: "Manual geometry drift",
      detail: "Atrium boundary 7F2A changed after snapshot r39.",
      sessionIds: ["option-b"],
      resource: "Rhino object · 7F2A",
    },
  ],
  lastUpdatedAt: now.toISOString(),
};

const clone = <T,>(value: T): T => structuredClone(value);

export function createMockApiClient(): GptinoApiClient {
  let state = clone(initialState);
  const listeners = new Set<(next: RuntimeState) => void>();

  const emit = () => {
    state.lastUpdatedAt = new Date().toISOString();
    const snapshot = clone(state);
    listeners.forEach((listener) => listener(snapshot));
  };

  const mutateSession = (id: string, mutate: (index: number) => void) => {
    const index = state.sessions.findIndex((session) => session.id === id);
    if (index >= 0) mutate(index);
    emit();
  };

  return {
    demo: true,
    async getRuntime() {
      await delay(80);
      return clone(state);
    },
    subscribe(onState) {
      listeners.add(onState);
      return () => listeners.delete(onState);
    },
    async createSession(name: string) {
      await delay();
      const ordinal = state.sessions.length + 1;
      state.sessions.push({
        id: `session-${crypto.randomUUID()}`,
        title: name || `New session ${ordinal}`,
        summary: "Ready for a modeling request",
        status: "idle",
        mode: "auto",
        modelProfile: "auto",
        paused: false,
        messages: [],
      });
      state.orderVersion += 1;
      emit();
    },
    async reorderSessions(request: SessionOrderRequest) {
      await delay();
      const positions = new Map(request.orderedSessionIds.map((id, index) => [id, index]));
      state.sessions.sort(
        (a, b) => (positions.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (positions.get(b.id) ?? Number.MAX_SAFE_INTEGER),
      );
      state.orderVersion += 1;
      emit();
    },
    async setSessionPaused(sessionId, paused) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].paused = paused;
        state.sessions[index].status = paused ? "paused" : "idle";
      });
    },
    async setSessionMode(sessionId, mode: SessionMode) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].mode = mode;
      });
    },
    async setSessionModel(sessionId, modelProfile: ModelProfile) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].modelProfile = modelProfile;
      });
    },
    async sendMessage(sessionId, request: MessageRequest) {
      await delay(220);
      mutateSession(sessionId, (index) => {
        const message: ChatMessage = {
          id: request.clientMessageId ?? crypto.randomUUID(),
          role: "user",
          content: request.content,
          createdAt: new Date().toISOString(),
        };
        state.sessions[index].messages.push(message);
        state.sessions[index].status = state.sessions[index].paused ? "paused" : "drafting";
      });
    },
    async openTerminal(sessionId) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].terminalOpen = true;
      });
    },
    async setRuntimePaused(paused) {
      await delay();
      state.paused = paused;
      emit();
    },
    async stopCurrent() {
      await delay(250);
      if (state.writer) {
        const index = state.sessions.findIndex((session) => session.id === state.writer?.sessionId);
        if (index >= 0) {
          state.sessions[index].status = "paused";
          state.sessions[index].paused = true;
        }
        state.queue = state.queue.filter((item) => item.id !== state.writer?.jobId);
        state.writer = undefined;
      }
      emit();
    },
  };
}
