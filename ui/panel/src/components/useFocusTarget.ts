import { useCallback, useEffect, useRef, useState } from "react";
import type { FocusMode, FocusResult } from "../types";

/**
 * The one implementation of GPTino's click-to-viewport contract, shared by every surface
 * that points at Rhino geometry (chat focus chips, the audit card's Show-in-Rhino rows,
 * and future card surfaces). Owning it in one place keeps three behaviours identical:
 *
 *  - result phrasing ("2 selected · 1 already gone · 42 hidden"),
 *  - isolation bookkeeping — the server keeps ONE restore stack, so a surface that hid
 *    anything must offer restore and must not strand the document,
 *  - unmount cleanup — closing the card / switching sessions restores automatically.
 *
 * Results are keyed so a surface with many targets (audit findings) can show a note per
 * row; single-target surfaces just use one key.
 */
export function useFocusTarget(onFocus?: (objectIds: string[], mode: FocusMode) => Promise<FocusResult>) {
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [isolating, setIsolating] = useState(false);

  const focus = useCallback(
    async (key: string, objectIds: string[], mode: FocusMode) => {
      if (!onFocus) return;
      setBusyKey(key);
      try {
        const outcome = await onFocus(objectIds, mode);
        setIsolating(outcome.hiddenCount > 0 || outcome.lockedCount > 0);
        const parts = [`${outcome.selectedCount} selected`];
        // A reference can outlive its objects; saying so beats an empty zoom the user
        // has to interpret.
        if (outcome.missingCount > 0) parts.push(`${outcome.missingCount} already gone`);
        if (outcome.hiddenCount > 0) parts.push(`${outcome.hiddenCount} hidden`);
        if (outcome.lockedCount > 0) parts.push(`${outcome.lockedCount} locked`);
        setNotes((current) => ({ ...current, [key]: parts.join(" · ") }));
      } catch (cause) {
        setNotes((current) => ({
          ...current,
          [key]: cause instanceof Error ? cause.message : String(cause),
        }));
      } finally {
        setBusyKey(null);
      }
    },
    [onFocus],
  );

  const restore = useCallback(async () => {
    if (!onFocus) return;
    setBusyKey("restore");
    try {
      await onFocus([], "restore");
      setIsolating(false);
      setNotes({});
    } finally {
      setBusyKey(null);
    }
  }, [onFocus]);

  // The ref keeps the cleanup from re-running on every focus press.
  const isolatingRef = useRef(false);
  isolatingRef.current = isolating;
  useEffect(
    () => () => {
      if (isolatingRef.current) void onFocus?.([], "restore");
    },
    [onFocus],
  );

  return { focus, restore, busyKey, notes, isolating, available: Boolean(onFocus) };
}
