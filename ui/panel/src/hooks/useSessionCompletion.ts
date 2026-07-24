import { useCallback, useEffect, useRef, useState } from "react";
import { notificationsSupported } from "../notifications";
import type { GptinoSession, RuntimeHealth, RuntimeState, SessionStatus } from "../types";

export type CompletionKind = "success" | "attention";

/** A completion the user has not yet acknowledged, rendered as a toast. */
export interface CompletionToast {
  id: string;
  sessionId: string;
  title: string;
  kind: CompletionKind;
  detail: string;
  at: number;
}

/** The bare completion signal before it is turned into a toast/notification. */
export interface CompletionEvent {
  sessionId: string;
  title: string;
  kind: CompletionKind;
  detail: string;
}

/** Statuses that count as "the session is doing work". A transition OUT of one of
 *  these into a terminal status is a completion. `paused` is deliberately excluded:
 *  it is user-initiated and must never notify. `drafting`/`verifying` are only ever
 *  seen from the mock server, but partition them here so the rule is total. */
const ACTIVE: ReadonlySet<SessionStatus> = new Set<SessionStatus>([
  "working",
  "drafting",
  "queued",
  "verifying",
]);

const DETAIL_MAX = 140;
/** Success toasts self-dismiss; attention toasts persist until dismissed. */
const SUCCESS_TTL_MS = 6_000;
/** Suppress a duplicate completion for the same session (post-action refetch can
 *  deliver a stale "working" snapshot after the SSE "idle", re-arming the edge). */
const DEDUPE_MS = 2_000;

const truncate = (value: string, max: number): string =>
  value.length > max ? `${value.slice(0, max - 1)}…` : value;

function detailFor(session: GptinoSession, runtime: RuntimeState, kind: CompletionKind): string {
  if (kind === "attention") {
    const conflict = runtime.conflicts.find((entry) => entry.sessionIds.includes(session.id));
    if (conflict?.detail) return truncate(conflict.detail, DETAIL_MAX);
  }
  for (let i = session.messages.length - 1; i >= 0; i -= 1) {
    const message = session.messages[i];
    if (message.role === "assistant" && message.content) return truncate(message.content, DETAIL_MAX);
  }
  if (session.summary) return truncate(session.summary, DETAIL_MAX);
  return "";
}

export interface CompletionDetection {
  events: CompletionEvent[];
  /** Status of every session in this snapshot — becomes the next `prevStatus` map.
   *  Rebuilt from the snapshot each time, so deleted sessions drop out for free. */
  nextStatus: Map<string, SessionStatus>;
}

/**
 * Pure edge detector — no DOM, no React, unit-testable. Fires a completion only
 * when a session goes from a known ACTIVE previous status to a terminal status
 * (idle = success, blocked = attention), AND both the previous and current health
 * are "connected". That single health gate delivers all three false-positive guards:
 *   - first sight of a session: prev is undefined → seed silently, no fire.
 *   - a session created mid-flight: same unknown-id path → seed silently.
 *   - reconnect after an AgentHost restart: the disconnected snapshot reseeds
 *     statuses with prevHealth != connected, so a session that comes back "idle"
 *     does not announce a completion it did not just perform.
 */
export function detectCompletions(
  prevStatus: ReadonlyMap<string, SessionStatus>,
  prevHealth: RuntimeHealth | null,
  runtime: RuntimeState,
): CompletionDetection {
  const events: CompletionEvent[] = [];
  const nextStatus = new Map<string, SessionStatus>();
  const bothConnected = prevHealth === "connected" && runtime.health === "connected";

  for (const session of runtime.sessions) {
    const prev = prevStatus.get(session.id);
    nextStatus.set(session.id, session.status);
    const kind: CompletionKind | null =
      session.status === "idle" ? "success" : session.status === "blocked" ? "attention" : null;
    if (prev !== undefined && ACTIVE.has(prev) && kind !== null && bothConnected) {
      events.push({ sessionId: session.id, title: session.title, kind, detail: detailFor(session, runtime, kind) });
    }
  }

  return { events, nextStatus };
}

export interface SessionCompletion {
  toasts: CompletionToast[];
  dismissToast(id: string): void;
  unseen: ReadonlySet<string>;
  markSeen(sessionId: string): void;
}

/**
 * Watches the server-truth runtime stream for session completions and surfaces
 * them as toasts, an "unseen" set (for the canvas unread dot), and — when the panel
 * is not focused on that session — a browser notification.
 */
export function useSessionCompletion(
  serverRuntime: RuntimeState | null,
  selectedId: string | null,
  onSelect: (id: string) => void,
): SessionCompletion {
  const [toasts, setToasts] = useState<CompletionToast[]>([]);
  const [unseen, setUnseen] = useState<Set<string>>(() => new Set());

  const prevStatus = useRef<Map<string, SessionStatus>>(new Map());
  const prevHealth = useRef<RuntimeHealth | null>(null);
  // toastId -> auto-dismiss timer handle (success toasts only).
  const timers = useRef<Map<string, number>>(new Map());
  // sessionId -> last fire timestamp, for the dedupe window.
  const lastFire = useRef<Map<string, number>>(new Map());
  // Read the latest selection / callback without re-running the detection effect.
  const selectedRef = useRef(selectedId);
  const onSelectRef = useRef(onSelect);
  selectedRef.current = selectedId;
  onSelectRef.current = onSelect;

  const clearTimer = useCallback((toastId: string) => {
    const handle = timers.current.get(toastId);
    if (handle !== undefined) {
      window.clearTimeout(handle);
      timers.current.delete(toastId);
    }
  }, []);

  const dismissToast = useCallback(
    (id: string) => {
      clearTimer(id);
      setToasts((current) => current.filter((toast) => toast.id !== id));
    },
    [clearTimer],
  );

  const markSeen = useCallback((sessionId: string) => {
    setUnseen((current) => {
      if (!current.has(sessionId)) return current;
      const next = new Set(current);
      next.delete(sessionId);
      return next;
    });
  }, []);

  useEffect(() => {
    if (!serverRuntime) return;

    const { events, nextStatus } = detectCompletions(prevStatus.current, prevHealth.current, serverRuntime);
    prevStatus.current = nextStatus;
    prevHealth.current = serverRuntime.health;

    // Prune unseen ids for sessions that no longer exist (deleted mid-flight).
    const presentIds = new Set(serverRuntime.sessions.map((session) => session.id));
    setUnseen((current) => {
      let changed = false;
      const next = new Set<string>();
      for (const id of current) {
        if (presentIds.has(id)) next.add(id);
        else changed = true;
      }
      return changed ? next : current;
    });

    if (events.length === 0) return;

    const hasFocus = typeof document !== "undefined" && document.hasFocus();
    const docHidden =
      typeof document === "undefined" || document.visibilityState === "hidden" || !document.hasFocus();
    const now = Date.now();

    for (const event of events) {
      const previousFire = lastFire.current.get(event.sessionId);
      if (previousFire !== undefined && now - previousFire < DEDUPE_MS) continue;
      lastFire.current.set(event.sessionId, now);

      // The user is literally watching this session: keep the toast, but do not mark
      // it unread and do not raise an OS notification.
      const watchingHere = event.sessionId === selectedRef.current && hasFocus;

      const toast: CompletionToast = { id: crypto.randomUUID(), ...event, at: now };
      setToasts((current) => {
        // Replace any undismissed toast for the same session so retries do not stack.
        const stale = current.filter((existing) => existing.sessionId === event.sessionId);
        stale.forEach((existing) => clearTimer(existing.id));
        return [...current.filter((existing) => existing.sessionId !== event.sessionId), toast];
      });
      if (toast.kind === "success") {
        const handle = window.setTimeout(() => {
          timers.current.delete(toast.id);
          setToasts((current) => current.filter((entry) => entry.id !== toast.id));
        }, SUCCESS_TTL_MS);
        timers.current.set(toast.id, handle);
      }

      if (!watchingHere) {
        setUnseen((current) => {
          if (current.has(event.sessionId)) return current;
          const next = new Set(current);
          next.add(event.sessionId);
          return next;
        });
      }

      if (docHidden) fireNotification(event, onSelectRef.current);
    }
  }, [serverRuntime, clearTimer]);

  // Clear any pending auto-dismiss timers on unmount.
  useEffect(() => {
    const pending = timers.current;
    return () => {
      pending.forEach((handle) => window.clearTimeout(handle));
      pending.clear();
    };
  }, []);

  return { toasts, dismissToast, unseen, markSeen };
}

function fireNotification(event: CompletionEvent, onSelect: (id: string) => void): void {
  if (!notificationsSupported() || Notification.permission !== "granted") return;
  try {
    const title =
      event.kind === "success" ? `${event.title} — finished` : `${event.title} — needs attention`;
    const notification = new Notification(title, {
      body: event.detail || undefined,
      // One live notification per session; a retry collapses onto the same one.
      tag: `gptino-${event.sessionId}`,
    });
    notification.onclick = () => {
      try {
        window.focus();
      } catch {
        // focus() can throw in some embedded hosts; selecting still works.
      }
      onSelect(event.sessionId);
    };
  } catch {
    // Notification construction can throw inside a Rhino WebView — degrade to toast/dot.
  }
}
