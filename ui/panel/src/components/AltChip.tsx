import type { FocusMode, FocusResult } from "../types";
import { useFocusTarget } from "./useFocusTarget";

/**
 * The alternative half of GPTino's conversational primitives: the agent proposes options
 * ("alt 1: upsize the girder", "alt 2: add a support") and each one is clickable, so the
 * user sees the variant instead of imagining it. When the marker carries objectIds
 * (`[[alt:id@guid,…|label]]` — the ids of the alt's baked preview geometry), clicking
 * ISOLATES those objects through the same useFocusTarget contract as focus chips, so
 * "보여줘" actually shows. Without ids the chip only reports the selection to its owner,
 * who switches whatever preview the task uses. Restore policy stays with the owner —
 * several chips share one server-side restore stack.
 */
interface AltChipProps {
  altId: string;
  label: string;
  active?: boolean;
  objectIds?: string[];
  onSelect(altId: string): void;
  onFocus?(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /** Reports after each call whether the document is now isolated/locked. */
  onIsolated?(isolating: boolean): void;
}

export function AltChip({ altId, label, active = false, objectIds, onSelect, onFocus, onIsolated }: AltChipProps) {
  const target = useFocusTarget(onFocus ?? (() => Promise.reject(new Error("no viewport"))));
  const canFocus = onFocus !== undefined && objectIds !== undefined && objectIds.length > 0;
  const note = canFocus ? target.notes.chip : undefined;

  return (
    <span className="focus-chip-wrap">
      <button
        type="button"
        className={`alt-chip${active ? " active" : ""}`}
        disabled={canFocus && target.busyKey !== null}
        onClick={() => {
          onSelect(altId);
          if (canFocus) {
            void target.focus("chip", objectIds!, "isolate").then(() => onIsolated?.(true));
          }
        }}
        title={
          canFocus
            ? `대안 "${label}"의 미리보기 ${objectIds!.length}개 객체만 뷰포트에 표시`
            : `대안 "${altId}"을 뷰포트에서 보기`
        }
      >
        <span aria-hidden="true">◆</span>
        {label}
      </button>
      {note ? <span className="focus-chip-note">{note}</span> : null}
    </span>
  );
}
