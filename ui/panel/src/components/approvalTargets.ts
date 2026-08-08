import type { ApprovalItem, ApprovalTarget } from "../types";

/**
 * Presentation model for one approval-card target row. Kept as a pure projection (no JSX) so the
 * omission rules are testable: a row exists only when the model actually authored something to
 * show (label/role/impact) — legacy cards whose targets are bare (objectId, fingerprint) pairs
 * render exactly as before, with no target rows at all.
 */
export interface ApprovalTargetRow {
  key: string;
  /** Prominent name; falls back to a short objectId prefix when the model gave role/impact but no label. */
  heading: string;
  role?: string;
  impact?: string;
  /**
   * Exactly what gets handed to the existing canvas-focus channel (POST /canvas/focus, the same
   * mechanism [[ghfocus:…]] chips use) when the zoom chip is pressed: this one target's objectId.
   */
  zoomObjectIds: string[];
}

function authored(value: string | null | undefined): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function hasPresentation(target: ApprovalTarget): boolean {
  return authored(target.label) || authored(target.role) || authored(target.impact);
}

export function approvalTargetRows(item: ApprovalItem): ApprovalTargetRow[] {
  // Duplicate objectIds within one item are legal input (a sloppy card can repeat a target);
  // React keys must still be unique, so repeats get a stable index suffix. The first
  // occurrence keeps the bare key, so well-formed cards render with unchanged keys.
  const seen = new Map<string, number>();
  return item.targets.filter(hasPresentation).map((target) => {
    const occurrence = seen.get(target.objectId) ?? 0;
    seen.set(target.objectId, occurrence + 1);
    return {
      key:
        occurrence === 0
          ? `${item.id}:${target.objectId}`
          : `${item.id}:${target.objectId}#${occurrence}`,
      heading: authored(target.label) ? target.label : target.objectId.slice(0, 8),
      role: authored(target.role) ? target.role : undefined,
      impact: authored(target.impact) ? target.impact : undefined,
      zoomObjectIds: [target.objectId],
    };
  });
}
