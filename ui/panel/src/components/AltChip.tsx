import type { MouseEvent } from "react";

/**
 * The alternative half of GPTino's conversational primitives: the agent proposes options
 * ("alt 1: upsize the girder", "alt 2: add a support") and each one is clickable, so the
 * user sees the variant instead of imagining it. The panel stays deliberately ignorant of
 * what an alt means — it hands `altId` to the owner, who switches whatever preview the
 * task uses (for structural work, the deformed-model visualization).
 */
interface AltChipProps {
  altId: string;
  label: string;
  active?: boolean;
  onSelect(altId: string): void;
}

export function AltChip({ altId, label, active = false, onSelect }: AltChipProps) {
  return (
    <button
      type="button"
      className={`alt-chip${active ? " active" : ""}`}
      onClick={(event: MouseEvent<HTMLButtonElement>) => {
        event.preventDefault();
        onSelect(altId);
      }}
      title={`대안 "${altId}"을 뷰포트에서 보기`}
    >
      <span aria-hidden="true">◆</span>
      {label}
    </button>
  );
}
