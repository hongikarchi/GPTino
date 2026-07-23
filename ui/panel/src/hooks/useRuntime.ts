import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createApiClient, createMockApiClient, type GptinoApiClient } from "../api/client";
import { moveById, shiftById } from "../order";
import type { ModelInfo, ModelProfile, RuntimeState, SessionMode } from "../types";

type OptimisticUpdate = (current: RuntimeState) => RuntimeState;

export function useRuntime() {
  const clientRef = useRef<GptinoApiClient | null>(null);
  if (!clientRef.current) clientRef.current = createApiClient();
  const client = clientRef.current;

  const [runtime, setRuntime] = useState<RuntimeState | null>(null);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyActions, setBusyActions] = useState<Set<string>>(() => new Set());

  const replaceClient = useCallback((next: GptinoApiClient) => {
    clientRef.current = next;
  }, []);

  useEffect(() => {
    let disposed = false;
    let unsubscribe: () => void = () => undefined;

    const connect = async (activeClient: GptinoApiClient) => {
      try {
        const initial = await activeClient.getRuntime();
        if (disposed) return;
        setRuntime(initial);
        setLoading(false);
        setError(null);
        void activeClient
          .listModels()
          .then((catalog) => {
            if (!disposed) setModels(catalog);
          })
          .catch(() => {
            // The model catalog is optional UI sugar; routing still works without it.
          });
        unsubscribe = activeClient.subscribe(
          (next) => {
            if (!disposed) setRuntime(next);
          },
          (subscriptionError) => {
            if (!disposed) setError(subscriptionError.message);
          },
        );
      } catch (initialError) {
        if (disposed) return;
        if (import.meta.env.DEV && !activeClient.demo) {
          const mock = createMockApiClient();
          replaceClient(mock);
          setError("AgentHost is unavailable — showing demo data.");
          await connect(mock);
          return;
        }
        setError(initialError instanceof Error ? initialError.message : "Unable to connect to GPTino");
        setLoading(false);
      }
    };

    void connect(clientRef.current!);
    return () => {
      disposed = true;
      unsubscribe();
    };
  }, [replaceClient]);

  const runAction = useCallback(
    async (key: string, optimistic: OptimisticUpdate | undefined, action: (client: GptinoApiClient) => Promise<void>) => {
      const before = runtime;
      if (optimistic) setRuntime((current) => (current ? optimistic(current) : current));
      setBusyActions((current) => new Set(current).add(key));
      setError(null);
      try {
        await action(clientRef.current!);
        const next = await clientRef.current!.getRuntime();
        setRuntime(next);
      } catch (actionError) {
        if (before) setRuntime(before);
        setError(actionError instanceof Error ? actionError.message : "The GPTino action failed");
      } finally {
        setBusyActions((current) => {
          const next = new Set(current);
          next.delete(key);
          return next;
        });
      }
    },
    [runtime],
  );

  const reorder = useCallback(
    (sourceId: string, targetId: string) => {
      if (!runtime || sourceId === targetId) return;
      const sessions = moveById(runtime.sessions, sourceId, targetId);
      const request = {
        orderedSessionIds: sessions.map(({ id }) => id),
        orderVersion: runtime.orderVersion,
      };
      void runAction(
        "reorder",
        (current) => ({ ...current, sessions }),
        (activeClient) => activeClient.reorderSessions(request),
      );
    },
    [runAction, runtime],
  );

  const shift = useCallback(
    (sessionId: string, direction: -1 | 1) => {
      if (!runtime) return;
      const sessions = shiftById(runtime.sessions, sessionId, direction);
      if (sessions.every((session, index) => session.id === runtime.sessions[index]?.id)) return;
      const target = sessions.findIndex(({ id }) => id === sessionId);
      const displaced = runtime.sessions[target]?.id;
      if (displaced) reorder(sessionId, displaced);
    },
    [reorder, runtime],
  );

  const updateSession = useCallback(
    (sessionId: string, update: (session: RuntimeState["sessions"][number]) => RuntimeState["sessions"][number]) =>
      (current: RuntimeState): RuntimeState => ({
        ...current,
        sessions: current.sessions.map((session) => (session.id === sessionId ? update(session) : session)),
      }),
    [],
  );

  const actions = useMemo(
    () => ({
      createSession(name: string) {
        return runAction("create-session", undefined, (activeClient) => activeClient.createSession(name));
      },
      reorder,
      shift,
      pauseSession(sessionId: string, paused: boolean) {
        return runAction(
          `pause:${sessionId}`,
          updateSession(sessionId, (session) => ({
            ...session,
            paused,
            status: paused ? "paused" : "idle",
          })),
          (activeClient) => activeClient.setSessionPaused(sessionId, paused),
        );
      },
      setMode(sessionId: string, mode: SessionMode) {
        return runAction(
          `mode:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, mode })),
          (activeClient) => activeClient.setSessionMode(sessionId, mode),
        );
      },
      setModel(sessionId: string, modelProfile: ModelProfile, model?: string | null) {
        return runAction(
          `model:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, modelProfile, pinnedModel: model ?? null })),
          (activeClient) => activeClient.setSessionModel(sessionId, modelProfile, model),
        );
      },
      async sendMessage(sessionId: string, content: string) {
        const clientMessageId = crypto.randomUUID();
        const createdAt = new Date().toISOString();
        return runAction(
          `message:${sessionId}`,
          updateSession(sessionId, (session) => ({
            ...session,
            status: session.paused ? session.status : "drafting",
            messages: [
              ...session.messages,
              { id: clientMessageId, role: "user", content, createdAt, pending: true },
            ],
          })),
          (activeClient) => activeClient.sendMessage(sessionId, { content, clientMessageId }),
        );
      },
      openTerminal(sessionId: string) {
        return runAction(
          `terminal:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, terminalOpen: true })),
          (activeClient) => activeClient.openTerminal(sessionId),
        );
      },
      pauseRuntime(paused: boolean) {
        return runAction(
          "pause-runtime",
          (current) => ({ ...current, paused }),
          (activeClient) => activeClient.setRuntimePaused(paused),
        );
      },
      openLoginTerminal() {
        return runAction("login-terminal", undefined, (activeClient) => activeClient.openLoginTerminal());
      },
      stopCurrent() {
        return runAction("stop-current", undefined, (activeClient) => activeClient.stopCurrent());
      },
    }),
    [reorder, runAction, shift, updateSession],
  );

  return {
    runtime,
    models,
    loading,
    error,
    demo: clientRef.current.demo,
    busyActions,
    actions,
  };
}
