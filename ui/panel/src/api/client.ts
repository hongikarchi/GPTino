import type {
  ArchiveMessage,
  ArchiveProject,
  DeletedSession,
  MessageRequest,
  ModelInfo,
  ModelProfile,
  RuntimeState,
  SessionMode,
  SessionOrderRequest,
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
  createSession(name: string, grasshopperDoc?: string): Promise<void>;
  reorderSessions(request: SessionOrderRequest): Promise<void>;
  setSessionPaused(sessionId: string, paused: boolean): Promise<void>;
  /** Stop the current turn and pull the last user message back for editing; returns its text. */
  retractLastMessage(sessionId: string): Promise<string | null>;
  /** Bind (docKey) or unbind (null) the GH document this session's writes target. */
  setSessionTarget(sessionId: string, grasshopperDoc: string | null): Promise<void>;
  setSessionMode(sessionId: string, mode: SessionMode): Promise<void>;
  setSessionModel(sessionId: string, modelProfile: ModelProfile, model?: string | null): Promise<void>;
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
  stopCurrent(): Promise<void>;
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

  createSession(name: string, grasshopperDoc?: string): Promise<void> {
    return this.request("/sessions", {
      method: "POST",
      body: JSON.stringify({
        name,
        role: "modeler",
        // New sessions default to Deep quality on the GPT-5.6-Sol model (see also mock.ts).
        modelProfile: "deep",
        model: "gpt-5.6-sol",
        ...(grasshopperDoc ? { grasshopperDoc } : {}),
      }),
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

  stopCurrent(): Promise<void> {
    return this.request("/runtime/stop-current", { method: "POST" });
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
