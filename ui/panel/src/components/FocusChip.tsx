import { useState } from "react";
import type { FocusMode, FocusResult } from "../types";

/**
 * The clickable half of GPTino's focus-reference primitive: one chip = one set of Rhino
 * objects the conversation is talking about. Click cycles the viewport onto them
 * (select+zoom by default; isolate via the small mode toggle), mirroring the audit
 * card's Show-in-Rhino state machine so the two surfaces feel identical. The chip only
 * reports whether it left the document isolated — restore policy (header button,
 * unmount cleanup) belongs to the owner, because several chips share one server-side
 * restore stack.
 */
interface FocusChipProps {
  objectIds: string[];
  label: string;
  onFocus(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /** Reports after each call whether the document is now isolated/locked. */
  onIsolated?(isolating: boolean): void;
}

export function FocusChip({ objectIds, label, onFocus, onIsolated }: FocusChipProps) {
  const [busy, setBusy] = useState(false);
  const [isolate, setIsolate] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const activate = async () => {
    setBusy(true);
    try {
      const outcome = await onFocus(objectIds, isolate ? "isolate" : "select");
      onIsolated?.(outcome.hiddenCount > 0 || outcome.lockedCount > 0);
      const parts = [`${outcome.selectedCount} selected`];
      if (outcome.missingCount > 0) parts.push(`${outcome.missingCount} already gone`);
      if (outcome.hiddenCount > 0) parts.push(`${outcome.hiddenCount} hidden`);
      setNote(parts.join(" · "));
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
        className="focus-chip"
        disabled={busy}
        onClick={() => void activate()}
        title={`${objectIds.length}개 객체를 뷰포트에서 확인`}
      >
        <span aria-hidden="true">◎</span>
        {label}
      </button>
      <button
        type="button"
        className={`focus-chip-mode${isolate ? " active" : ""}`}
        disabled={busy}
        onClick={() => setIsolate((v) => !v)}
        title={isolate ? "클릭: 선택+줌만" : "클릭: 나머지 숨기고 보기 (isolate)"}
      >
        iso
      </button>
      {note ? <span className="focus-chip-note">{note}</span> : null}
    </span>
  );
}
