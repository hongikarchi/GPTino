import type {
  ApprovalGrant,
  ArchiveMessage,
  ArchiveProject,
  DataFlowDetail,
  DeletedSession,
  RhinoAuditKind,
  RhinoAuditResult,
  MessageRequest,
  ModelInfo,
  ModelProfile,
  RuntimeState,
  SessionMode,
  SessionOrderRequest,
  SessionRole,
  FocusMode,
  FocusResult,
} from "../types";
import { createMockApiClient } from "./mock";

export interface GptinoApiClient {
  readonly demo: boolean;
  getRuntime(): Promise<RuntimeState>;
  subscribe(
    onState: (state: RuntimeState) => void,
    onError?: (error: Error) => void,
  ): () => void;
  listModels(): Promise<ModelInfo[]>;
  createSession(name: string, grasshopperDoc?: string, role?: SessionRole): Promise<void>;
  /** On-demand Rhino<->GH data-flow detail for one GH doc (omit docId when only one is open). */
  focusObjects(objectIds: string[], mode: FocusMode, zoom?: boolean): Promise<FocusResult>;
  /** Prose language for GPTino's answers ("ko" | "en"); UI labels stay English either way. */
  getLanguage(): Promise<{ language: string }>;
  setLanguage(language: string): Promise<{ language: string }>;
  getDataFlowDetail(docId?: string | null): Promise<DataFlowDetail>;
  /** Server-computed document-hygiene audit (deterministic; the card renders it verbatim). */
  getAudit(kind: RhinoAuditKind, options?: { tolerance?: number; bandFactor?: number; limit?: number }): Promise<RhinoAuditResult>;
  /** Mint a user approval bound to the exact (objectId, fingerprint) pairs shown on the card. */
  mintApprovalGrant(items: { objectId: string; fingerprint: string }[]): Promise<ApprovalGrant>;
  reorderSessions(request: SessionOrderRequest): Promise<void>;
  setSessionPaused(sessionId: string, paused: boolean): Promise<void>;
  /** Stop the current turn and pull the last user message back for editing; returns its text. */
  retractLastMessage(sessionId: string): Promise<string | null>;
  /** Bind (docKey) or unbind (null) the GH document this session's writes target. */
  setSessionTarget(sessionId: string, grasshopperDoc: string | null): Promise<void>;
  setSessionMode(sessionId: string, mode: SessionMode): Promise<void>;
  setSessionModel(sessionId: string, modelProfile: ModelProfile, model?: string | null): Promise<void>;
  /** Toggle the session's native Codex thread goal (objective + budget) on/off. */
  setSessionGoal(sessionId: string, enabled: boolean): Promise<void>;
  sendMessage(sessionId: string, request: MessageRequest): Promise<void>;
  /** Soft-delete: hide from the active list, recoverable from the trash. */
  deleteSession(sessionId: string): Promise<void>;
  restoreSession(sessionId: string): Promise<void>;
  /** Permanent delete: removes the session and its transcript for good. */
  purgeSession(sessionId: string): Promise<void>;
  listDeletedSessions(): Promise<DeletedSession[]>;
  openTerminal(sessionId: string): Promise<void>;
  openLoginTerminal(): Promise<void>;
  setRuntimePaused(paused: boolean): Promise<void>;
  listArchive(): Promise<ArchiveProject[]>;
  readArchiveMessages(fingerprint: string, sessionId: string, limit?: number): Promise<ArchiveMessage[]>;
  /** Fork an archived session into the current project as a new live session (new thread, seeded context). */
  importArchiveSession(fingerprint: string, sessionId: string): Promise<void>;
}

const trimTrailingSlash = (value: string) => value.replace(/\/+$/, "");

function configuredApiBase(): string {
  const query = new URLSearchParams(window.location.search).get("apiBase");
  return trimTrailingSlash(query ?? window.__GPTINO__?.apiBase ?? "");
}

function demoRequested(): boolean {
  const query = new URLSearchParams(window.location.search);
  return query.get("demo") === "1" || window.__GPTINO__?.demo === true;
}

class HttpApiClient implements GptinoApiClient {
  readonly demo = false;
  private readonly base: string;

  constructor(base: string) {
    this.base = `${base}/api/v1`;
  }

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(`${this.base}${path}`, {
      credentials: "same-origin",
      ...init,
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });

    if (!response.ok) {
      // A 401 here is never something the user did wrong: the panel authenticates with a
      // cookie minted by a one-time bootstrap nonce, so the session goes stale whenever the
      // AgentHost it was minted for is gone (Rhino restarted, another instance took the
      // port). Raw server JSON told the user nothing actionable; name the fix instead.
      if (response.status === 401) {
        throw new Error(
          "패널 세션이 만료됐습니다 (이 런타임의 토큰이 아닙니다). 패널을 닫았다가 " +
            "GPTinoOpenPanel로 다시 열면 복구됩니다.",
        );
      }
      const detail = await response.text();
      throw new Error(detail || `GPTino API returned ${response.status}`);
    }

    if (response.status === 204 || response.headers.get("content-length") === "0") {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  getRuntime(): Promise<RuntimeState> {
    return this.request<RuntimeState>("/runtime");
  }

  listModels(): Promise<ModelInfo[]> {
    return this.request<ModelInfo[]>("/models");
  }

  subscribe(
    onState: (state: RuntimeState) => void,
    onError?: (error: Error) => void,
  ): () => void {
    let disposed = false;
    let pollingTimer: number | undefined;
    let events: EventSource | undefined;

    const poll = async () => {
      try {
        onState(await this.getRuntime());
      } catch (error) {
        onError?.(error instanceof Error ? error : new Error("Runtime polling failed"));
      }
    };

    const startPolling = () => {
      if (disposed || pollingTimer !== undefined) return;
      void poll();
      pollingTimer = window.setInterval(() => void poll(), 1_500);
    };

    if (typeof EventSource === "undefined") {
      startPolling();
    } else {
      events = new EventSource(`${this.base}/events`, { withCredentials: true });
      const handleState = (event: MessageEvent<string>) => {
        try {
          onState(JSON.parse(event.data) as RuntimeState);
        } catch {
          onError?.(new Error("GPTino sent an invalid runtime event"));
        }
      };
      events.onmessage = handleState;
      events.addEventListener("state", handleState as EventListener);
      events.onerror = () => {
        events?.close();
        events = undefined;
        startPolling();
      };
    }

    return () => {
      disposed = true;
      events?.close();
      if (pollingTimer !== undefined) window.clearInterval(pollingTimer);
    };
  }

  reorderSessions(request: SessionOrderRequest): Promise<void> {
    return this.request("/sessions/order", {
      method: "PUT",
      body: JSON.stringify(request),
    });
  }

  createSession(name: string, grasshopperDoc?: string, role?: SessionRole): Promise<void> {
    return this.request("/sessions", {
      method: "POST",
      body: JSON.stringify({
        name,
        role: role ?? "modeler",
        // New sessions default to xhigh reasoning effort on the GPT-5.6-Sol model (see also mock.ts).
        modelProfile: "xhigh",
        model: "gpt-5.6-sol",
        ...(grasshopperDoc ? { grasshopperDoc } : {}),
      }),
    });
  }

  getLanguage(): Promise<{ language: string }> {
    return this.request<{ language: string }>("/language");
  }

  setLanguage(language: string): Promise<{ language: string }> {
    return this.request<{ language: string }>("/language", {
      method: "POST",
      body: JSON.stringify({ language }),
    });
  }

  focusObjects(objectIds: string[], mode: FocusMode, zoom = true): Promise<FocusResult> {
    return this.request<FocusResult>("/focus", {
      method: "POST",
      body: JSON.stringify({ objectIds, mode, zoom }),
    });
  }

  getDataFlowDetail(docId?: string | null): Promise<DataFlowDetail> {
    const query = docId ? `?doc=${encodeURIComponent(docId)}` : "";
    return this.request<DataFlowDetail>(`/data-flow${query}`);
  }

  async getAudit(
    kind: RhinoAuditKind,
    options?: { tolerance?: number; bandFactor?: number; limit?: number },
  ): Promise<RhinoAuditResult> {
    const query = new URLSearchParams({ kind });
    if (options?.tolerance != null) query.set("tolerance", String(options.tolerance));
    if (options?.bandFactor != null) query.set("bandFactor", String(options.bandFactor));
    if (options?.limit != null) query.set("limit", String(options.limit));
    // The backend wraps bridge reads as { result, fingerprint, diagnostics }.
    const wrapped = await this.request<{ result: RhinoAuditResult }>(`/audit?${query}`);
    return wrapped.result;
  }

  mintApprovalGrant(items: { objectId: string; fingerprint: string }[]): Promise<ApprovalGrant> {
    return this.request<ApprovalGrant>("/approval-grants", {
      method: "POST",
      body: JSON.stringify({ items }),
    });
  }

  setSessionPaused(sessionId: string, paused: boolean): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/pause`, {
      method: "PUT",
      body: JSON.stringify({ paused }),
    });
  }

  async retractLastMessage(sessionId: string): Promise<string | null> {
    const result = await this.request<{ content: string | null }>(
      `/sessions/${encodeURIComponent(sessionId)}/retract-last`,
      { method: "POST" },
    );
    return result?.content ?? null;
  }

  setSessionTarget(sessionId: string, grasshopperDoc: string | null): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/target`, {
      method: "PUT",
      body: JSON.stringify({ grasshopperDoc }),
    });
  }

  setSessionMode(sessionId: string, mode: SessionMode): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/mode`, {
      method: "PUT",
      body: JSON.stringify({ mode }),
    });
  }

  setSessionModel(sessionId: string, modelProfile: ModelProfile, model?: string | null): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/model`, {
      method: "PUT",
      body: JSON.stringify({ modelProfile, model: model ?? null }),
    });
  }

  setSessionGoal(sessionId: string, enabled: boolean): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/goal`, {
      method: "PUT",
      body: JSON.stringify({ enabled }),
    });
  }

  sendMessage(sessionId: string, request: MessageRequest): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/messages`, {
      method: "POST",
      body: JSON.stringify(request),
    });
  }

  deleteSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}`, { method: "DELETE" });
  }

  restoreSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/restore`, { method: "POST" });
  }

  purgeSession(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/purge`, { method: "DELETE" });
  }

  listDeletedSessions(): Promise<DeletedSession[]> {
    return this.request<DeletedSession[]>("/sessions/deleted");
  }

  openTerminal(sessionId: string): Promise<void> {
    return this.request(`/sessions/${encodeURIComponent(sessionId)}/terminal`, {
      method: "POST",
    });
  }

  openLoginTerminal(): Promise<void> {
    return this.request("/runtime/login-terminal", { method: "POST" });
  }

  setRuntimePaused(paused: boolean): Promise<void> {
    return this.request("/runtime/pause", {
      method: "PUT",
      body: JSON.stringify({ paused }),
    });
  }

  listArchive(): Promise<ArchiveProject[]> {
    return this.request<ArchiveProject[]>("/archive");
  }

  readArchiveMessages(fingerprint: string, sessionId: string, limit = 500): Promise<ArchiveMessage[]> {
    return this.request<ArchiveMessage[]>(
      `/archive/${encodeURIComponent(fingerprint)}/sessions/${encodeURIComponent(sessionId)}/messages?limit=${limit}`,
    );
  }

  importArchiveSession(fingerprint: string, sessionId: string): Promise<void> {
    return this.request(
      `/archive/${encodeURIComponent(fingerprint)}/sessions/${encodeURIComponent(sessionId)}/import`,
      { method: "POST" },
    );
  }
}

export function createApiClient(): GptinoApiClient {
  if (demoRequested()) return createMockApiClient();
  return new HttpApiClient(configuredApiBase());
}

export { createMockApiClient };
