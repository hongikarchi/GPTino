import type { SessionStatus } from "../types";

const labels: Record<SessionStatus, string> = {
  working: "Working",
  drafting: "Drafting",
  queued: "Queued",
  verifying: "Verifying",
  paused: "Paused",
  blocked: "Blocked",
  idle: "Idle",
};

export function StatusBadge({ status }: { status: SessionStatus }) {
  return (
    <span className={`status-badge status-${status}`}>
      <span className="status-dot" />
      {labels[status]}
    </span>
  );
}
