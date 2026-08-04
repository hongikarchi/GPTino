import { useState } from "react";
import type { FocusMode, FocusResult } from "../types";
import { useFocusTarget } from "./useFocusTarget";

/**
 * The clickable half of GPTino's focus-reference primitive: one chip = one set of Rhino
 * objects the conversation is talking about. Click points the viewport at them (select+
 * zoom by default; isolate via the small toggle). Behaviour comes from useFocusTarget so
 * chat chips and audit-card rows stay identical; restore policy belongs to the owner
 * (several chips share one server-side restore stack), which is why isolation is
 * reported upward rather than handled here.
 */
interface FocusChipProps {
  objectIds: string[];
  label: string;
  onFocus(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /** Reports after each call whether the document is now isolated/locked. */
  onIsolated?(isolating: boolean): void;
}

export function FocusChip({ objectIds, label, onFocus, onIsolated }: FocusChipProps) {
  const [isolate, setIsolate] = useState(false);
  const target = useFocusTarget(onFocus);
  const busy = target.busyKey !== null;
  const note = target.notes.chip;

  const activate = async () => {
    await target.focus("chip", objectIds, isolate ? "isolate" : "select");
  };

  return (
    <span className="focus-chip-wrap">
      <button
        type="button"
        className="focus-chip"
        disabled={busy}
        onClick={() => {
          void activate().then(() => onIsolated?.(isolate));
        }}
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
