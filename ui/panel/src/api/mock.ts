import type { GptinoApiClient } from "./client";
import type {
  ArchiveMessage,
  ArchiveProject,
  ChatMessage,
  DataFlowDetail,
  DeletedSession,
  MessageRequest,
  MessageRole,
  ModelInfo,
  ModelProfile,
  RuntimeState,
  SessionOrderRequest,
  FocusMode,
} from "../types";

/** A goal card mid-conversation so the demo shows the frame-before-you-build flow. */
const demoGoalCard = JSON.stringify({
  status: "proposing",
  objective: "남측 파사드 패널을 네 가지 패밀리로 줄이되 주 그리드는 유지한다",
  criteria: [
    "패널 패밀리 수 <= 4 (컴포넌트 출력 카운트로 확인)",
    "주 그리드 교점 좌표가 현재와 동일 (스냅샷 대조)",
    "모든 컴포넌트가 런타임 오류 없이 커밋",
  ],
  assumptions: ["'남측'은 Facade::South 레이어를 뜻한다", "개구부 비율은 현재 값을 유지"],
  outOfScope: ["구조 검토", "베이크"],
  options: [
    { id: "approve", label: "이대로 진행", detail: "위 기준으로 작업을 시작합니다" },
    {
      id: "keep-openings",
      label: "개구부도 재계산",
      detail: "패밀리 축소에 맞춰 개구부 비율까지 다시 잡습니다",
      objectIds: ["a0b1c2d3-0001-4e4e-9f9f-000000000001"],
    },
    { id: "narrow", label: "한 베이만 시범", detail: "먼저 한 베이에만 적용해 결과를 봅니다" },
  ],
});
/** An approval card so the demo shows the approve-what-you-saw flow for user-owned geometry. */
const demoApprovalCard = JSON.stringify({
  status: "proposing",
  summary: "근사 중복 2쌍과 끝점 갭 1곳을 고치려 합니다 — 사용자가 그린 기하라 승인이 필요합니다.",
  items: [
    {
      id: "dup-1",
      label: "Facade::Boundary 커브 2개가 겹칩니다",
      measure: "간격 0.08 mm (허용오차 0.001 mm)",
      targets: [
        { objectId: "a0b1c2d3-0001-4e4e-9f9f-000000000001", fingerprint: "fp-a1" },
        { objectId: "a0b1c2d3-0002-4e4e-9f9f-000000000002", fingerprint: "fp-a2" },
      ],
      choices: ["첫 번째를 남김", "두 번째를 남김"],
    },
    {
      id: "gap-1",
      label: "Atrium::Edge 끝점이 만나지 않습니다",
      measure: "간격 0.42 mm",
      targets: [{ objectId: "a0b1c2d3-0003-4e4e-9f9f-000000000003", fingerprint: "fp-b1" }],
    },
  ],
});

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

// Durable 16-hex docKeys for the two demo Grasshopper documents.
const DOC_FACADE = "4f2a91c7d05e83b6";
const DOC_ATRIUM = "b81d4e9a2c7f5063";

const demoState: RuntimeState = {
  projectId: "prj-tower-a31f924c",
  projectName: "Tower study / Option A",
  rhinoFile: "Tower_Study_A.3dm",
  grasshopperFile: "Facade_Paneling.gh",
  grasshopperDocs: [
    { id: DOC_FACADE, file: "C:\\Projects\\Tower\\Facade_Paneling.gh" },
    { id: DOC_ATRIUM, file: "C:\\Projects\\Tower\\Atrium_Options.gh" },
  ],
  health: "connected",
  healthDetail: "Rhino 8 · Grasshopper attached",
  revision: 42,
  gitRevision: 17,
  orderVersion: 8,
  paused: false,
  codexLimits: {
    updatedAt: minutesAgo(2),
    windows: [
      { label: "5h", usedPercent: 34, resetsAt: minutesAgo(-140) },
      { label: "weekly", usedPercent: 12, resetsAt: minutesAgo(-4_320) },
    ],
  },
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
      goalCard: demoGoalCard,
      modelProfile: "xhigh",
      pinnedModel: "gpt-5.6-sol",
      backend: "codex",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "xhigh",
      paused: false,
      terminalOpen: true,
      unread: 1,
      boundGrasshopperDocId: DOC_FACADE,
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
      modelProfile: "low",
      effectiveModel: "gpt-5.6-terra",
      reasoning: "low",
      paused: false,
      boundGrasshopperDocId: DOC_FACADE,
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
      modelProfile: "medium",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "medium",
      paused: true,
      boundGrasshopperDocId: null,
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
      modelProfile: "xhigh",
      effectiveModel: "gpt-5.6-sol",
      reasoning: "xhigh",
      paused: false,
      boundGrasshopperDocId: DOC_ATRIUM,
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
    {
      // An ordinary session doing document care — the same session model as every other row.
      id: "document-care",
      title: "Document care",
      approvalCard: demoApprovalCard,
      summary: "Rhino document hygiene",
      status: "idle",
      modelProfile: "xhigh",
      paused: false,
      boundGrasshopperDocId: null,
      messages: [
        {
          id: "m-c-1",
          role: "user",
          content: "Run a full document check-up.",
          createdAt: minutesAgo(52),
        },
        {
          id: "m-c-2",
          role: "assistant",
          content:
            "Check-up complete (tolerance 0.001 mm). Endpoint gaps: 2 near-miss pairs (largest 0.42 mm) " +
            "[[focus:a0b1c2d3-0001-4e4e-9f9f-000000000001,a0b1c2d3-0002-4e4e-9f9f-000000000002|끝점 갭 2쌍 보기]] on Facade::Boundary. " +
            "Near-duplicates: 1 candidate pair on Atrium::Boundary — which copy to keep is your call. " +
            "Purge candidates: 3 unused block definitions, 1 empty leaf layer. Data ledger: 2 broken references in Atrium_Options.gh. " +
            "Nothing was changed; tell me which findings to fix.",
          createdAt: minutesAgo(50),
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
      targetDocId: DOC_FACADE,
    },
    {
      id: "job-185",
      sessionId: "wires",
      title: "Connect staged sockets",
      state: "ready",
      resource: "GH · 3 wires",
      target: "grasshopper",
      targetDocId: DOC_FACADE,
    },
    {
      id: "job-187",
      sessionId: "option-b",
      title: "Regenerate atrium boundary",
      state: "waiting",
      waitingFor: "Manual drift resolution",
      resource: "Rhino · object 7F2A",
      target: "rhino",
      targetDocId: null,
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
    docId: DOC_FACADE,
    observedAt: minutesAgo(0),
  },
  dataFlow: [
    {
      docId: DOC_FACADE,
      referenceCount: 12,
      missingReferenceCount: 0,
      bakeCount: 38,
      revision: 42,
      observedAt: minutesAgo(1),
    },
    {
      // Tied to the existing drift-conflict fixture: object 7F2A changed after snapshot r39,
      // and two of the atrium references now point at deleted Rhino objects.
      docId: DOC_ATRIUM,
      referenceCount: 5,
      missingReferenceCount: 2,
      bakeCount: 0,
      revision: 39,
      observedAt: minutesAgo(4),
    },
  ],
  unattributedBakeCount: 3,
  lastUpdatedAt: now.toISOString(),
};

/** Demo-only prose-language preference so the header toggle round-trips without a server. */
let demoLanguage = "en";



const demoDataFlowDetails: Record<string, DataFlowDetail> = {
  [DOC_FACADE]: {
    docId: DOC_FACADE,
    revision: 42,
    observedAt: minutesAgo(1),
    references: {
      grasshopperDocumentId: "c1d2e3f4-0000-4a4a-b1b1-00000000d0c1",
      referenceCount: 12,
      missingCount: 0,
      parameters: [
        {
          parameterId: "c1d2e3f4-0000-4a4a-b1b1-000000000010",
          nickName: "Facade Srf",
          parameterType: "Param_Surface",
          objects: [
            {
              rhinoObjectId: "b2416cd8-55f7-4f39-a9d3-08a1c4e7d992",
              exists: true,
              layerFullPath: "Facade::Input",
            },
          ],
        },
        {
          parameterId: "c1d2e3f4-0000-4a4a-b1b1-000000000011",
          nickName: "Boundary Crvs",
          parameterType: "Param_Curve",
          objects: Array.from({ length: 11 }, (_, index) => ({
            rhinoObjectId: `a0b1c2d3-00${index.toString().padStart(2, "0")}-4e4e-9f9f-0000000000${index
              .toString(16)
              .padStart(2, "0")}`,
            exists: true,
            layerFullPath: "Facade::Boundary",
          })),
        },
      ],
    },
    bakes: {
      totalStamped: 41,
      groups: [
        {
          sourceDocKey: DOC_FACADE,
          bakeFamily: "facade-panels",
          count: 38,
          objectIds: ["7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01"],
        },
        { sourceDocKey: null, bakeFamily: "legacy-massing", count: 3, objectIds: [] },
      ],
    },
  },
  [DOC_ATRIUM]: {
    docId: DOC_ATRIUM,
    revision: 39,
    observedAt: minutesAgo(4),
    references: {
      grasshopperDocumentId: "c1d2e3f4-0000-4a4a-b1b1-00000000d0c2",
      referenceCount: 5,
      missingCount: 2,
      parameters: [
        {
          parameterId: "c1d2e3f4-0000-4a4a-b1b1-000000000020",
          nickName: "Atrium Boundary",
          parameterType: "Param_Curve",
          objects: [
            {
              rhinoObjectId: "7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01",
              exists: true,
              layerFullPath: "Atrium::Boundary",
            },
            { rhinoObjectId: "d4e5f6a7-1111-4b4b-8c8c-000000000001", exists: false, layerFullPath: null },
            { rhinoObjectId: "d4e5f6a7-1111-4b4b-8c8c-000000000002", exists: false, layerFullPath: null },
          ],
        },
        {
          parameterId: "c1d2e3f4-0000-4a4a-b1b1-000000000021",
          nickName: "Roof Pts",
          parameterType: "Param_Point",
          objects: [
            {
              rhinoObjectId: "e5f6a7b8-2222-4c4c-9d9d-000000000001",
              exists: true,
              layerFullPath: "Atrium::Points",
            },
            {
              rhinoObjectId: "e5f6a7b8-2222-4c4c-9d9d-000000000002",
              exists: true,
              layerFullPath: "Atrium::Points",
            },
          ],
        },
      ],
    },
    bakes: {
      // The stamped census is Rhino-document-wide, so the facade family appears here too —
      // the drawer's own-doc filter is what scopes the view, not the payload.
      totalStamped: 41,
      groups: [
        {
          sourceDocKey: DOC_FACADE,
          bakeFamily: "facade-panels",
          count: 38,
          objectIds: ["7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01"],
        },
        { sourceDocKey: null, bakeFamily: "legacy-massing", count: 3, objectIds: [] },
      ],
    },
  },
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
  const deleted: DeletedSession[] = [];

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
    async focusObjects(objectIds: string[], mode: FocusMode, zoom = true) {
      await delay(60);
      return {
        selectedCount: mode === "restore" ? 0 : objectIds.length,
        missingCount: 0,
        hiddenCount: mode === "isolate" ? 42 : 0,
        lockedCount: mode === "lock" ? 42 : 0,
        restored: mode === "restore",
        fingerprint: `focus-${mode}-${objectIds.length}-${zoom ? "z" : "n"}`,
      };
    },
    async getLanguage() {
      await delay(30);
      return { language: demoLanguage };
    },
    async setLanguage(language: string) {
      await delay(30);
      demoLanguage = language === "ko" ? "ko" : "en";
      return { language: demoLanguage };
    },
    async getDataFlowDetail(docId?: string | null) {
      await delay(160);
      const detail = demoDataFlowDetails[docId ?? DOC_FACADE];
      if (!detail) {
        throw new Error(`Unknown Grasshopper document '${docId}'.`);
      }
      return clone(detail);
    },
    async createSession(name: string, grasshopperDoc?: string) {
      await delay();
      const ordinal = state.sessions.length + 1;
      state.sessions.push({
        id: `session-${crypto.randomUUID()}`,
        title: name || `New session ${ordinal}`,
        summary: "Ready for a modeling request",
        status: "idle",
        modelProfile: "xhigh",
        pinnedModel: "gpt-5.6-sol",
        paused: false,
        messages: [],
        boundGrasshopperDocId: grasshopperDoc ?? null,
      });
      state.orderVersion += 1;
      emit();
    },
    async setSessionTarget(sessionId, grasshopperDoc) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].boundGrasshopperDocId = grasshopperDoc;
      });
    },
    async deleteSession(sessionId) {
      await delay();
      const index = state.sessions.findIndex((session) => session.id === sessionId);
      if (index >= 0) {
        const [removed] = state.sessions.splice(index, 1);
        deleted.unshift({
          id: removed.id,
          name: removed.title,
          state: removed.status,
          updatedAt: new Date().toISOString(),
        });
        state.orderVersion += 1;
        emit();
      }
    },
    async restoreSession(sessionId) {
      await delay();
      const index = deleted.findIndex((session) => session.id === sessionId);
      if (index >= 0) {
        const [restored] = deleted.splice(index, 1);
        state.sessions.push({
          id: restored.id,
          title: restored.name,
          summary: "Restored session",
          status: "idle",
          modelProfile: "xhigh",
          paused: false,
          messages: [],
          boundGrasshopperDocId: null,
        });
        state.orderVersion += 1;
        emit();
      }
    },
    async purgeSession(sessionId) {
      await delay();
      const index = deleted.findIndex((session) => session.id === sessionId);
      if (index >= 0) deleted.splice(index, 1);
    },
    async listDeletedSessions() {
      await delay(60);
      return deleted.map((session) => ({ ...session }));
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
    async retractLastMessage(sessionId) {
      await delay();
      const session = state.sessions.find((item) => item.id === sessionId);
      if (!session) return null;
      let index = -1;
      for (let i = session.messages.length - 1; i >= 0; i--) {
        if (session.messages[i].role === "user") {
          index = i;
          break;
        }
      }
      if (index < 0) return null;
      const content = session.messages[index].content;
      session.messages = session.messages.slice(0, index);
      session.status = "idle";
      emit();
      return content;
    },
    async setSessionModel(sessionId, modelProfile: ModelProfile, model?: string | null) {
      await delay();
      mutateSession(sessionId, (index) => {
        state.sessions[index].modelProfile = modelProfile;
        state.sessions[index].pinnedModel = model ?? null;
        if (model) state.sessions[index].effectiveModel = model;
      });
    },
    async answerApprovalCard(
      sessionId: string,
      answer: { status: "granted" | "rejected"; approvedItemIds?: string[]; choices?: Record<string, string> },
    ) {
      await delay();
      mutateSession(sessionId, (index) => {
        const raw = state.sessions[index].approvalCard;
        if (!raw) return;
        const card = JSON.parse(raw);
        state.sessions[index].approvalCard = JSON.stringify({
          ...card,
          status: answer.status,
          approvedItemIds: answer.approvedItemIds ?? [],
          grantId: answer.status === "granted" ? "demo-grant-0001" : null,
        });
      });
    },
    async answerGoalCard(
      sessionId: string,
      answer: { status: "confirmed" | "rejected"; chosenOption?: string; objective?: string; criteria?: string[] },
    ) {
      await delay();
      mutateSession(sessionId, (index) => {
        const raw = state.sessions[index].goalCard;
        if (!raw) return;
        const card = JSON.parse(raw);
        state.sessions[index].goalCard = JSON.stringify({
          ...card,
          status: answer.status,
          objective: answer.objective ?? card.objective,
          criteria: answer.criteria ?? card.criteria,
          chosenOption: answer.chosenOption ?? card.chosenOption,
        });
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
      // Demo only: a live AgentHost drives the real drafting→idle transition. Here we
      // fake it after a beat so ?demo=1 exercises the completion toast, browser
      // notification, and canvas unread dot end-to-end.
      window.setTimeout(() => {
        mutateSession(sessionId, (index) => {
          const session = state.sessions[index];
          if (session.paused || session.status !== "drafting") return;
          session.status = "idle";
          session.messages.push({
            id: crypto.randomUUID(),
            role: "assistant",
            content: "Done — the requested change is applied and verified. Revision committed.",
            createdAt: new Date().toISOString(),
          });
        });
      }, 4_000);
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
    async importArchiveSession(fingerprint: string, sessionId: string) {
      await delay(220);
      // Mirror the server: a NEW session named "<name> (imported)" with a leading stale-reference
      // banner and the copied transcript — no thread carried over, ready for a fresh first turn.
      const project = demoArchive.find((entry) => entry.fingerprint === fingerprint);
      const archived = project?.sessions.find((entry) => entry.id === sessionId);
      const label = project?.projectName ?? fingerprint;
      const copied = demoArchiveMessages[`${fingerprint}/${sessionId}`] ?? [];
      const banner: ChatMessage = {
        id: `m-import-${crypto.randomUUID()}`,
        role: "system",
        content:
          `Imported from project '${label}' (${fingerprint}), session '${archived?.name ?? sessionId}'. ` +
          "This history was recorded against a different Rhino/Grasshopper document: every component " +
          "id, wire, and geometry reference below is stale. Re-discover the current canvas state via a " +
          "snapshot before acting on any of it.",
        createdAt: new Date().toISOString(),
      };
      state.sessions.push({
        id: `session-${crypto.randomUUID()}`,
        title: `${archived?.name ?? "Imported session"} (imported)`,
        summary: "Imported from a past project",
        status: "idle",
        modelProfile: "xhigh",
        paused: false,
        boundGrasshopperDocId: null,
        messages: [
          banner,
          ...copied.map((message) => ({
            id: `m-import-${crypto.randomUUID()}`,
            role: message.role as MessageRole,
            content: message.content,
            createdAt: message.createdAt,
          })),
        ],
      });
      state.orderVersion += 1;
      emit();
    },
  };
}
