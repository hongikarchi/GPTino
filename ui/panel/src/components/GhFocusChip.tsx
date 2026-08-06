import { useState } from "react";
import type { CanvasFocusResult } from "../types";

/**
 * The Grasshopper-canvas twin of FocusChip: one chip = a set of components GPTino worked on.
 * Click selects them on the GH canvas and frames them in the viewport (POST /canvas/focus).
 * Unlike the Rhino FocusChip there is no isolate/lock — a canvas has no "hide the rest" notion —
 * so this is a plain select+zoom with no shared restore stack to manage.
 */
interface GhFocusChipProps {
  objectIds: string[];
  label: string;
  onFocusCanvas(objectIds: string[]): Promise<CanvasFocusResult>;
}

export function GhFocusChip({ objectIds, label, onFocusCanvas }: GhFocusChipProps) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const activate = async () => {
    if (busy) return;
    setBusy(true);
    setNote(null);
    try {
      const outcome = await onFocusCanvas(objectIds);
      // A referenced component can be gone (user deleted it since) — say so rather than leaving an
      // empty frame the user has to interpret.
      setNote(
        outcome.missingCount > 0
          ? `${outcome.selectedCount} 선택 · ${outcome.missingCount} 사라짐`
          : `${outcome.selectedCount} 선택`,
      );
    } catch (cause) {
      setNote(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="focus-chip-wrap">
      <button
        type="button"
        className="focus-chip gh"
        disabled={busy}
        onClick={() => void activate()}
        title={`${objectIds.length}개 컴포넌트를 Grasshopper 캔버스에서 확인`}
      >
        <span aria-hidden="true">◇</span>
        {label}
      </button>
      {note ? <span className="focus-chip-note">{note}</span> : null}
    </span>
  );
}
