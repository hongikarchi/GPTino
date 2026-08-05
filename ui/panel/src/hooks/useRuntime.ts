import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createApiClient, createMockApiClient, type GptinoApiClient } from "../api/client";
import { moveById, shiftById } from "../order";
import type {
  FocusMode,
  MessageAttachment,
  ModelInfo,
  ModelProfile,
  RhinoAuditKind,
  RuntimeState,
  SessionMode,
} from "../types";

type OptimisticUpdate = (current: RuntimeState) => RuntimeState;

export function useRuntime() {
  const clientRef = useRef<GptinoApiClient | null>(null);
  if (!clientRef.current) clientRef.current = createApiClient();
  const client = clientRef.current;

  const [runtime, setRuntime] = useState<RuntimeState | null>(null);
  // Server-truth snapshot, set ONLY at the three points a snapshot arrives from the
  // server (initial load, subscribe callback, post-action refetch) — never on an
  // optimistic write or a rollback. The completion detector consumes this instead of
  // `runtime` so a client-only optimistic status (e.g. "drafting") can never manufacture
  // a false completion edge.
  const [serverRuntime, setServerRuntime] = useState<RuntimeState | null>(null);
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
        setServerRuntime(initial);
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
            if (!disposed) {
              setRuntime(next);
              setServerRuntime(next);
            }
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

  // Resolves true when the API call succeeded; callers that staged local state
  // (e.g. the composer draft) use the false result to restore it.
  const runAction = useCallback(
    async (key: string, optimistic: OptimisticUpdate | undefined, action: (client: GptinoApiClient) => Promise<void>): Promise<boolean> => {
      const before = runtime;
      if (optimistic) setRuntime((current) => (current ? optimistic(current) : current));
      setBusyActions((current) => new Set(current).add(key));
      setError(null);
      try {
        await action(clientRef.current!);
        const next = await clientRef.current!.getRuntime();
        setRuntime(next);
        setServerRuntime(next);
        return true;
      } catch (actionError) {
        // Rollback restores the optimistic-merged view only; serverRuntime is left
        // untouched so a failed action never emits a phantom completion edge.
        if (before) setRuntime(before);
        setError(actionError instanceof Error ? actionError.message : "The GPTino action failed");
        return false;
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
        // The resident curator is outside the draggable order: the server's exact-membership
        // check excludes it, so including its id here would 409 every drag.
        orderedSessionIds: sessions.filter(({ role }) => role !== "curator").map(({ id }) => id),
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

  // Archive reads never mutate runtime state, so they bypass runAction's
  // optimistic-update / refetch machinery. Stable identities on purpose: the
  // archive overlay keys its fetch effects on these, and they must not re-fire
  // every time a runtime event re-memoizes `actions`.
  const listArchive = useCallback(() => clientRef.current!.listArchive(), []);
  const listDeleted = useCallback(() => clientRef.current!.listDeletedSessions(), []);
  // Read-only like the archive callbacks: the data-flow drawer keys its fetch effect on this.
  const focusObjects = useCallback(
    (objectIds: string[], mode: FocusMode) => clientRef.current!.focusObjects(objectIds, mode),
    [],
  );

  const getDataFlowDetail = useCallback(
    (docId?: string | null) => clientRef.current!.getDataFlowDetail(docId),
    [],
  );

  // Prose language for GPTino's answers. Project-level (not per session) and applied when
  // the next thread starts/resumes, so the toggle reports optimistically and never blocks.
  const [language, setLanguageState] = useState("en");
  useEffect(() => {
    let disposed = false;
    clientRef.current
      ?.getLanguage()
      .then((value) => {
        if (!disposed) setLanguageState(value.language === "ko" ? "ko" : "en");
      })
      .catch(() => undefined);
    return () => {
      disposed = true;
    };
  }, []);
  const setLanguage = useCallback(async (next: string) => {
    setLanguageState(next === "ko" ? "ko" : "en");
    try {
      const applied = await clientRef.current!.setLanguage(next);
      setLanguageState(applied.language === "ko" ? "ko" : "en");
    } catch {
      // Leave the optimistic value; the next reload re-reads the server's answer.
    }
  }, []);
  // The audit card keys its fetch effect on this — stable identity required.
  const getAudit = useCallback(
    (kind: RhinoAuditKind, options?: { tolerance?: number; bandFactor?: number; limit?: number }) =>
      clientRef.current!.getAudit(kind, options),
    [],
  );
  const mintApprovalGrant = useCallback(
    (items: { objectId: string; fingerprint: string }[]) => clientRef.current!.mintApprovalGrant(items),
    [],
  );
  const readArchiveMessages = useCallback(
    (fingerprint: string, sessionId: string, limit?: number) =>
      clientRef.current!.readArchiveMessages(fingerprint, sessionId, limit),
    [],
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
      createSession(name: string, grasshopperDoc?: string) {
        return runAction("create-session", undefined, (activeClient) => activeClient.createSession(name, grasshopperDoc));
      },
      reorder,
      shift,
      setSessionTarget(sessionId: string, grasshopperDoc: string | null) {
        return runAction(
          `target:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, boundGrasshopperDocId: grasshopperDoc })),
          (activeClient) => activeClient.setSessionTarget(sessionId, grasshopperDoc),
        );
      },
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
      async retractLast(sessionId: string): Promise<string | null> {
        const content = await clientRef.current!.retractLastMessage(sessionId);
        const next = await clientRef.current!.getRuntime();
        setRuntime(next);
        setServerRuntime(next);
        return content;
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
      // The user's verdict on a proposed goal card. No optimistic patch: the server rewrites the
      // card (status + any edits) and the next SSE push is the truth — showing an approved card
      // before the server accepted it would be exactly the false-success this project refuses.
      answerGoal(
        sessionId: string,
        answer: { status: "confirmed" | "rejected"; chosenOption?: string; objective?: string; criteria?: string[] },
      ) {
        return runAction(
          `goal:${sessionId}`,
          undefined,
          (activeClient) => activeClient.answerGoalCard(sessionId, answer),
        );
      },
      async sendMessage(sessionId: string, content: string, attachments?: MessageAttachment[]) {
        const clientMessageId = crypto.randomUUID();
        const createdAt = new Date().toISOString();
        // The backend persists attachments as short "[Attached: name]" lines; the optimistic
        // message mirrors that so the transcript does not jump when the server row arrives.
        const attachmentLines = (attachments ?? []).map((attachment) => `[Attached: ${attachment.fileName}]`);
        const optimisticContent =
          attachmentLines.length === 0
            ? content
            : [content, ...attachmentLines].filter((line) => line.length > 0).join("\n");
        return runAction(
          `message:${sessionId}`,
          updateSession(sessionId, (session) => ({
            ...session,
            status: session.paused ? session.status : "drafting",
            messages: [
              ...session.messages,
              { id: clientMessageId, role: "user", content: optimisticContent, createdAt, pending: true },
            ],
          })),
          (activeClient) =>
            activeClient.sendMessage(sessionId, {
              content,
              clientMessageId,
              ...(attachments && attachments.length > 0 ? { attachments } : {}),
            }),
        );
      },
      openTerminal(sessionId: string) {
        return runAction(
          `terminal:${sessionId}`,
          updateSession(sessionId, (session) => ({ ...session, terminalOpen: true })),
          (activeClient) => activeClient.openTerminal(sessionId),
        );
      },
      deleteSession(sessionId: string) {
        return runAction(
          `delete:${sessionId}`,
          // Optimistically drop it from the visible list; the refetch confirms.
          (current) => ({ ...current, sessions: current.sessions.filter((session) => session.id !== sessionId) }),
          (activeClient) => activeClient.deleteSession(sessionId),
        );
      },
      restoreSession(sessionId: string) {
        return runAction(
          `restore:${sessionId}`,
          undefined,
          (activeClient) => activeClient.restoreSession(sessionId),
        );
      },
      purgeSession(sessionId: string) {
        return runAction(
          `purge:${sessionId}`,
          undefined,
          (activeClient) => activeClient.purgeSession(sessionId),
        );
      },
      listDeleted,
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
      listArchive,
      readArchiveMessages,
      focusObjects,
      setLanguage,
      getDataFlowDetail,
      getAudit,
      mintApprovalGrant,
      // Import mutates runtime state (a new session appears), so — unlike the read-only archive
      // callbacks — it goes through runAction whose post-action getRuntime() pulls the new session in.
      importArchiveSession(fingerprint: string, sessionId: string) {
        return runAction(
          `import:${sessionId}`,
          undefined,
          (activeClient) => activeClient.importArchiveSession(fingerprint, sessionId),
        );
      },
    }),
    [focusObjects, getAudit, getDataFlowDetail, listArchive, listDeleted, mintApprovalGrant, readArchiveMessages, reorder, runAction, setLanguage, shift, updateSession],
  );

  return {
    runtime,
    serverRuntime,
    models,
    loading,
    error,
    demo: clientRef.current.demo,
    busyActions,
    language,
    actions,
  };
}
