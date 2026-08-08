import { useState } from "react";
import type { ApprovalCard as ApprovalCardData, CanvasFocusResult, FocusMode, FocusResult } from "../types";
import { approvalTargetRows } from "./approvalTargets";
import { GhFocusChip } from "./GhFocusChip";
import { useFocusTarget } from "./useFocusTarget";

/**
 * Approve-what-you-saw for the user's OWN geometry. The broker refuses destructive ops on objects
 * GPTino did not create, and this card is the only way to lift that for specific objects: ticking
 * an item grants exactly its (objectId, fingerprint) pairs. Pinning to fingerprints is the point —
 * if the object moved after the audit, the grant no longer matches and the fix fails instead of
 * hitting something the user never saw. Choices exist where a machine must not decide (which of two
 * near-duplicates to keep is a design decision, not a cleanup).
 */
interface ApprovalCardProps {
  card: ApprovalCardData;
  busy?: boolean;
  onAnswer(answer: {
    status: "granted" | "rejected";
    approvedItemIds?: string[];
    choices?: Record<string, string>;
  }): void;
  onFocus?(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /**
   * The panel's existing Grasshopper canvas-focus channel (POST /canvas/focus — the same one
   * [[ghfocus:…]] chips use). Destructive-cleanup targets are GH components, so their zoom chips
   * go to the canvas, not the Rhino viewport. Optional — without it the chips simply don't render.
   */
  onFocusCanvas?(objectIds: string[]): Promise<CanvasFocusResult>;
}

export function ApprovalCard({ card, busy = false, onAnswer, onFocus, onFocusCanvas }: ApprovalCardProps) {
  const [checked, setChecked] = useState<Record<string, boolean>>({});
  const [choices, setChoices] = useState<Record<string, string>>({});
  const focus = useFocusTarget(onFocus);
  const answered = card.status !== "proposing";
  const approvedCount = card.items.filter((item) => checked[item.id]).length;

  return (
    <section className={`approval-card approval-${card.status}`} aria-label="변경 승인">
      <header className="goal-card-head">
        <strong>{answered ? "승인 결과" : "이 변경을 승인하시겠어요?"}</strong>
        {card.status === "granted" ? <span className="goal-card-badge">승인됨</span> : null}
        {card.status === "rejected" ? <span className="goal-card-badge">거절됨</span> : null}
      </header>
      <p className="goal-card-objective">{card.summary}</p>

      <ul className="approval-card-list">
        {card.items.map((item) => {
          const granted = card.approvedItemIds?.includes(item.id);
          const targetRows = approvalTargetRows(item);
          return (
            <li key={item.id} className={answered && granted ? "granted" : ""}>
              <label>
                {answered ? (
                  <span aria-hidden="true">{granted ? "✔ " : "· "}</span>
                ) : (
                  <input
                    type="checkbox"
                    checked={checked[item.id] ?? false}
                    disabled={busy}
                    onChange={(event) =>
                      setChecked((current) => ({ ...current, [item.id]: event.target.checked }))
                    }
                  />
                )}
                <span>{item.label}</span>
                {item.measure ? <span className="approval-card-measure"> {item.measure}</span> : null}
              </label>
              {onFocus ? (
                <button
                  type="button"
                  className="goal-card-show"
                  disabled={busy}
                  title="이 항목의 객체를 뷰포트에서 보기"
                  onClick={() =>
                    void focus.focus(item.id, item.targets.map((target) => target.objectId), "select")
                  }
                >
                  ◎
                </button>
              ) : null}
              {/* A choice only matters for an item the user is actually granting. */}
              {!answered && item.choices?.length && checked[item.id] ? (
                <span className="approval-card-choices" role="radiogroup" aria-label="어느 것을 남길까요">
                  {item.choices.map((choice) => (
                    <label key={choice}>
                      <input
                        type="radio"
                        name={`choice-${item.id}`}
                        checked={(choices[item.id] ?? item.choices![0]) === choice}
                        onChange={() => setChoices((current) => ({ ...current, [item.id]: choice }))}
                      />
                      {choice}
                    </label>
                  ))}
                </span>
              ) : null}
              {/* Model-authored per-target context so a destructive cleanup can actually be judged:
                  what each component is, what it does, and what changes if it goes. Legacy cards
                  (bare objectId+fingerprint targets) produce no rows and render exactly as before. */}
              {targetRows.length > 0 ? (
                <ul className="approval-card-targets">
                  {targetRows.map((row) => (
                    <li key={row.key}>
                      <span className="approval-target-head">
                        <strong className="approval-target-label">{row.heading}</strong>
                        {onFocusCanvas ? (
                          <GhFocusChip objectIds={row.zoomObjectIds} label="확대" onFocusCanvas={onFocusCanvas} />
                        ) : null}
                      </span>
                      {row.role ? <span className="approval-target-line">역할: {row.role}</span> : null}
                      {row.impact ? <span className="approval-target-line">변경: {row.impact}</span> : null}
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          );
        })}
      </ul>

      {!answered ? (
        <div className="goal-card-actions">
          <button
            type="button"
            className="goal-card-choose"
            disabled={busy || approvedCount === 0}
            title={approvedCount === 0 ? "고칠 항목을 하나 이상 선택하세요" : undefined}
            onClick={() =>
              onAnswer({
                status: "granted",
                approvedItemIds: card.items.filter((item) => checked[item.id]).map((item) => item.id),
                choices,
              })
            }
          >
            선택한 {approvedCount}개 승인
          </button>
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => onAnswer({ status: "rejected" })}
          >
            하지 마세요
          </button>
        </div>
      ) : null}
    </section>
  );
}
