import { useState } from "react";
import type { FocusMode, FocusResult, GoalCard as GoalCardData } from "../types";
import { useFocusTarget } from "./useFocusTarget";

/**
 * The goal card: what GPTino understood the request to be, shown BEFORE the work starts so
 * correcting it is cheap. Same approve-what-you-saw contract as the audit card — whatever the
 * user confirms here is what the agent is held to, and the closing self-score answers these
 * exact criteria. Options are the structured reply (approve / narrow / correct …); an option
 * carrying objectIds also points the viewport at the geometry it is about, which is the part a
 * plain chat question cannot do.
 */
interface GoalCardProps {
  card: GoalCardData;
  busy?: boolean;
  onAnswer(answer: {
    status: "confirmed" | "rejected";
    chosenOption?: string;
    objective?: string;
    criteria?: string[];
  }): void;
  onFocus?(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
}

export function GoalCard({ card, busy = false, onAnswer, onFocus }: GoalCardProps) {
  const [editing, setEditing] = useState(false);
  const [objective, setObjective] = useState(card.objective);
  const [criteria, setCriteria] = useState(card.criteria.join("\n"));
  const focus = useFocusTarget(onFocus);

  // Confirmed/scored cards are a record, not a prompt: show the verdict, take no more answers.
  const answered = card.status !== "proposing";

  return (
    <section className={`goal-card goal-${card.status}`} aria-label="목표 확인">
      <header className="goal-card-head">
        <strong>{answered ? "목표" : "이렇게 이해했습니다 — 맞나요?"}</strong>
        {card.status === "scored" ? <span className="goal-card-badge">채점됨</span> : null}
        {card.status === "confirmed" ? <span className="goal-card-badge">진행 중</span> : null}
      </header>

      {editing ? (
        <textarea
          className="goal-card-edit"
          value={objective}
          rows={2}
          onChange={(event) => setObjective(event.target.value)}
        />
      ) : (
        <p className="goal-card-objective">{card.objective}</p>
      )}

      <div className="goal-card-block">
        <span className="goal-card-label">이러면 성공</span>
        {editing ? (
          <textarea
            className="goal-card-edit"
            value={criteria}
            rows={Math.max(3, card.criteria.length)}
            onChange={(event) => setCriteria(event.target.value)}
          />
        ) : (
          <ul>
            {(card.scores ?? card.criteria.map((criterion) => ({ criterion, passed: null, evidence: "" }))).map(
              (row: { criterion: string; passed: boolean | null; evidence: string }, index: number) => (
                <li key={index} className={row.passed === null ? "" : row.passed ? "pass" : "fail"}>
                  {row.passed === null ? null : <span aria-hidden="true">{row.passed ? "✔ " : "✘ "}</span>}
                  {row.criterion}
                  {row.evidence ? <span className="goal-card-evidence"> — {row.evidence}</span> : null}
                </li>
              ),
            )}
          </ul>
        )}
      </div>

      {card.assumptions?.length ? (
        <div className="goal-card-block">
          <span className="goal-card-label">이렇게 가정했습니다</span>
          <ul>{card.assumptions.map((item, index) => <li key={index}>{item}</li>)}</ul>
        </div>
      ) : null}

      {card.outOfScope?.length ? (
        <div className="goal-card-block">
          <span className="goal-card-label">이번엔 안 합니다</span>
          <ul>{card.outOfScope.map((item, index) => <li key={index}>{item}</li>)}</ul>
        </div>
      ) : null}

      {!answered ? (
        <div className="goal-card-actions">
          {(card.options ?? [{ id: "approve", label: "이대로 진행" }]).map((option) => (
            <span key={option.id} className="goal-card-option">
              <button
                type="button"
                className="goal-card-choose"
                disabled={busy}
                title={option.detail ?? undefined}
                onClick={() =>
                  onAnswer({
                    status: "confirmed",
                    chosenOption: option.id,
                    objective: editing ? objective : undefined,
                    criteria: editing
                      ? criteria.split("\n").map((line) => line.trim()).filter(Boolean)
                      : undefined,
                  })
                }
              >
                {option.label}
              </button>
              {option.objectIds?.length && onFocus ? (
                <button
                  type="button"
                  className="goal-card-show"
                  disabled={busy}
                  title="이 선택지가 가리키는 객체를 뷰포트에서 보기"
                  onClick={() => void focus.focus(option.id, option.objectIds!, "select")}
                >
                  ◎
                </button>
              ) : null}
            </span>
          ))}
          <button type="button" className="secondary-button" disabled={busy} onClick={() => setEditing((v) => !v)}>
            {editing ? "편집 취소" : "고쳐서 승인"}
          </button>
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => onAnswer({ status: "rejected" })}
          >
            아니요
          </button>
        </div>
      ) : null}
    </section>
  );
}
