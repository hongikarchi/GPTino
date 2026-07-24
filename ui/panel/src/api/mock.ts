import type { GptinoApiClient } from "./client";
import type {
  ArchiveMessage,
  ArchiveProject,
  ChatMessage,
  MessageRequest,
  ModelInfo,
  ModelProfile,
  RuntimeState,
  SessionMode,
  SessionOrderRequest,
} from "../types";

const demoModels: ModelInfo[] = [
  {
    id: "gpt-5.6-sol",
    model: "gpt-5.6-sol",
    displayName: "GPT-5.6 Sol",
    description: "Strongest reasoning for geometry and recovery work",
    isDefault: true,
    reasoningEfforts: ["low", "medium", "high", "xhigh"],
  },
  {
    id: "gpt-5.6-terra",
    model: "gpt-5.6-terra",
    displayName: "GPT-5.6 Terra",
    description: "Balanced general modeling",
    isDefault: false,
    reasoningEfforts: ["low", "medium", "high"],
  },
  {
    id: "gpt-5.6-luna",
    model: "gpt-5.6-luna",
    displayName: "GPT-5.6 Luna",
    description: "Fast reads and simple typed operations",
    isDefault: false,
    reasoningEfforts: ["minimal", "low", "medium"],
  },
];

const now = new Date();
const minutesAgo = (minutes: number) => new Date(now.getTime() - minutes * 60_000).toISOString();
const delay = (ms = 120) => new Promise((resolve) => window.setTimeout(resolve, ms));

const demoState: RuntimeState = {
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
      pinnedModel: "gpt-5.6-sol",
      backend: "codex",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "xhigh",
      paused: false,
      terminalOpen: true,
      unread: 1,
      usage: {
        totalTokens: 812_400,
        contextWindow: 272_000,
        contextUsedTokens: 104_500,
        rateLimits: [
          { label: "5h", usedPercent: 34, resetsAt: minutesAgo(-140) },
          { label: "weekly", usedPercent: 12 },
        ],
      },
      currentActivity: "Submitting: Rebuild panel boundaries",
      activity: [
        { at: minutesAgo(13), kind: "turn", summary: "Turn started — gpt-5.6-sol (xhigh)", ok: true, durationMs: 0 },
        { at: minutesAgo(12), kind: "snapshot_read", summary: "Reading the canvas snapshot", ok: true, durationMs: 420 },
        { at: minutesAgo(11), kind: "skill_read", summary: "Reading skill bake_manager.py", ok: true, durationMs: 12 },
        { at: minutesAgo(9), kind: "artifact_write", summary: "Drafting operations/panel-remap.json", ok: true, durationMs: 8 },
        { at: minutesAgo(4), kind: "change_submit", summary: "Submitting: Rebuild panel boundaries", ok: true, durationMs: 260 },
      ],
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
      usage: {
        totalTokens: 96_100,
        contextWindow: 272_000,
        contextUsedTokens: 22_300,
      },
      activity: [
        { at: minutesAgo(9), kind: "turn", summary: "Turn started — gpt-5.6-terra (low)", ok: true, durationMs: 0 },
        { at: minutesAgo(8), kind: "change_submit", summary: "Submitting: Connect staged sockets", ok: true, durationMs: 180 },
      ],
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
      target: "grasshopper",
    },
    {
      id: "job-185",
      sessionId: "wires",
      title: "Connect staged sockets",
      state: "ready",
      resource: "GH · 3 wires",
      target: "grasshopper",
    },
    {
      id: "job-187",
      sessionId: "option-b",
      title: "Regenerate atrium boundary",
      state: "waiting",
      waitingFor: "Manual drift resolution",
      resource: "Rhino · object 7F2A",
      target: "rhino",
    },
  ],
  conflicts: [
    {
      id: "conflict-7",
      title: "Manual geometry drift",
      detail: "Atrium boundary 7F2A changed after snapshot r39.",
      sessionIds: ["option-b"],
      resource: "Rhino object · 7F2A",
      resolution:
        "Re-read the object to adopt the manual edit as the new baseline, or ask the session to regenerate from the current geometry.",
      observedAt: minutesAgo(35),
    },
    {
      id: "conflict-8",
      title: "Write overlap",
      detail: "Facade rationalization and wire cleanup both stage GH group 'panel-grid'.",
      sessionIds: ["facade", "wires"],
      resource: "GH group · panel-grid",
      resolution: "The queue serializes both writes; pause one session if the order matters.",
      observedAt: minutesAgo(8),
    },
  ],
  contextFolder: "C:\\Users\\user\\AppData\\Local\\GPTino\\projects\\a31f924c\\context",
  codexAuth: { status: "logged-out", detail: "Signed out — click to log in" },
  currentSelection: {
    rhinoObjectCount: 2,
    rhinoObjectIds: [
      "7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01",
      "b2416cd8-55f7-4f39-a9d3-08a1c4e7d992",
    ],
    activeLayer: "Facade::Panels",
    grasshopperObjectCount: 3,
    grasshopperObjects: [
      { id: "c1d2e3f4-0000-4a4a-b1b1-000000000001", nickName: "Panel Grid" },
      { id: "c1d2e3f4-0000-4a4a-b1b1-000000000002", nickName: "Offset" },
      { id: "c1d2e3f4-0000-4a4a-b1b1-000000000003", nickName: "Area" },
    ],
    observedAt: minutesAgo(0),
  },
  lastUpdatedAt: now.toISOString(),
};

const hoursAgo = (hours: number) => minutesAgo(hours * 60);
const daysAgo = (days: number) => hoursAgo(days * 24);

// Read-only archive demo data: the current root plus two orphaned roots — one
// readable (the pre-crash tower study) and one whose runtime.db cannot be opened.
const demoArchive: ArchiveProject[] = [
  {
    fingerprint: "A31F924C7D0B45E2",
    projectName: "Tower study / Option A",
    rhinoFile: "C:\\Projects\\Tower\\Tower_Study_A.3dm",
    grasshopperFile: "C:\\Projects\\Tower\\Facade_Paneling.gh",
    createdAt: daysAgo(6),
    lastActivityAt: minutesAgo(2),
    sessionCount: 2,
    current: true,
    available: true,
    sessions: [
      { id: "arch-cur-1", name: "Facade rationalization", state: "running", updatedAt: minutesAgo(2), messageCount: 3 },
      { id: "arch-cur-2", name: "Wire cleanup", state: "waiting", updatedAt: minutesAgo(8), messageCount: 2 },
    ],
  },
  {
    fingerprint: "5B8E02D1C4F7A960",
    projectName: "Tower_Study_A (autosave)",
    rhinoFile: "C:\\Projects\\Tower\\Tower_Study_A_recovered.3dm",
    grasshopperFile: "C:\\Projects\\Tower\\Facade_Paneling.gh",
    createdAt: daysAgo(3),
    lastActivityAt: daysAgo(1),
    sessionCount: 2,
    current: false,
    available: true,
    sessions: [
      { id: "arch-old-1", name: "Facade rationalization", state: "failed", updatedAt: daysAgo(1), messageCount: 4 },
      { id: "arch-old-2", name: "Atrium massing", state: "idle", updatedAt: daysAgo(2), messageCount: 2 },
    ],
  },
  {
    fingerprint: "9C4D77E10AB2F358",
    projectName: "Bridge deck concept",
    rhinoFile: "C:\\Projects\\Bridge\\Deck_Concept.3dm",
    grasshopperFile: "C:\\Projects\\Bridge\\Deck_Ribs.gh",
    createdAt: daysAgo(19),
    lastActivityAt: null,
    sessionCount: 0,
    current: false,
    available: false,
    sessions: [],
  },
];

const demoArchiveMessages: Record<string, ArchiveMessage[]> = {
  "5B8E02D1C4F7A960/arch-old-1": [
    { id: 1, role: "user", content: "Rationalize the facade into four repeatable panel families without moving the primary grid.", phase: null, createdAt: daysAgo(1.2) },
    { id: 2, role: "assistant", content: "I mapped 126 unique panels and staged a remap to four families in a shadow definition. Running the verification solve now.", phase: "draft", createdAt: daysAgo(1.15) },
    { id: 3, role: "assistant", content: "The shadow solve passed area and closure checks. Submitting the typed geometry changes to the live document.", phase: "commit", createdAt: daysAgo(1.1) },
    { id: 4, role: "system", content: "The previous turn was interrupted by an AgentHost restart; review the document state before retrying.", phase: "recovery", createdAt: daysAgo(1) },
  ],
  "5B8E02D1C4F7A960/arch-old-2": [
    { id: 5, role: "user", content: "Sketch two atrium massing options with the same usable floor area.", phase: null, createdAt: daysAgo(2.1) },
    { id: 6, role: "assistant", content: "Both options are drafted on the GPTino-managed layers; option two keeps the softer corner transition you asked about.", phase: null, createdAt: daysAgo(2) },
  ],
  "A31F924C7D0B45E2/arch-cur-1": [
    { id: 7, role: "user", content: "Keep the existing tower silhouette, but rationalize the facade into four repeatable panel families.", phase: null, createdAt: minutesAgo(18) },
    { id: 8, role: "assistant", content: "I found 126 unique panels driven mainly by edge tolerance. I can reduce them to four families without moving the primary grid.", phase: null, createdAt: minutesAgo(13) },
    { id: 9, role: "assistant", content: "The shadow solve passed. Applying the typed geometry changes before committing revision 18.", phase: "commit", createdAt: minutesAgo(2) },
  ],
  "A31F924C7D0B45E2/arch-cur-2": [
    { id: 10, role: "user", content: "Connect the three numbered outputs to the matching inputs and keep the existing data-tree paths.", phase: null, createdAt: minutesAgo(9) },
    { id: 11, role: "assistant", content: "Targets and socket types are unambiguous. The ChangeSet is ready and waiting for the current writer.", phase: null, createdAt: minutesAgo(8) },
  ],
};

const clone = <T,>(value: T): T => structuredClone(value);

export function createDemoRuntimeState(): RuntimeState {
  return clone(demoState);
}

export function createMockApiClient(): GptinoApiClient {
  let state = createDemoRuntimeState();
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
    async listModels() {
      await delay(60);
      return clone(demoModels);
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
    async setSessionModel(sessionId, modelProfile: ModelProfile, model?: string | null) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].modelProfile = modelProfile;
        state.sessions[index].pinnedModel = model ?? null;
        if (model) state.sessions[index].effectiveModel = model;
      });
    },
    async sendMessage(sessionId, request: MessageRequest) {
      await delay(220);
      mutateSession(sessionId, (index) => {
        // Mirror the AgentHost transcript shape: attachments become short "[Attached: name]" lines.
        const attachmentLines = (request.attachments ?? []).map(
          (attachment) => `[Attached: ${attachment.fileName}]`,
        );
        const content =
          attachmentLines.length === 0
            ? request.content
            : [request.content, ...attachmentLines].filter((line) => line.length > 0).join("\n");
        const message: ChatMessage = {
          id: request.clientMessageId ?? crypto.randomUUID(),
          role: "user",
          content,
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
    async openLoginTerminal() {
      await delay(400);
      state.codexAuth = { status: "logged-in", detail: "Signed in" };
      emit();
    },
    async setRuntimePaused(paused) {
      await delay();
      state.paused = paused;
      emit();
    },
    async listArchive() {
      await delay(160);
      return clone(demoArchive);
    },
    async readArchiveMessages(fingerprint: string, sessionId: string, limit = 500) {
      await delay(180);
      const messages = demoArchiveMessages[`${fingerprint}/${sessionId}`];
      if (!messages) {
        throw new Error(`Session ${sessionId} was not found in archive project '${fingerprint}'.`);
      }
      return clone(messages.slice(-limit));
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
