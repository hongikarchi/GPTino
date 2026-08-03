import { useEffect, useMemo, useState } from "react";
import type { RhinoAuditFinding, RhinoAuditKind, RhinoAuditResult } from "../types";

interface AuditCardProps {
  kind: RhinoAuditKind;
  runAudit(kind: RhinoAuditKind): Promise<RhinoAuditResult>;
  /**
   * Approve the checked findings: the caller mints a grant bound to exactly these
   * (objectId, fingerprint) pairs and instructs the curator to apply the fixes.
   * `keepFirst` maps findingId -> whether to KEEP the first listed object (duplicates only).
   */
  onApprove(result: RhinoAuditResult, approved: RhinoAuditFinding[], keepFirst: Record<string, boolean>): Promise<void>;
  onClose(): void;
  /** False renders report-only (no checkboxes/Approve) — for kinds whose fix ops ship later. */
  approvable?: boolean;
  /** True while the curator is busy/paused — Approve would mint a grant no turn could consume. */
  busy?: boolean;
}

const KIND_TITLES: Record<RhinoAuditKind, string> = {
  nearMissEndpoints: "Endpoint gaps",
  nearDuplicates: "Near-duplicates",
  purgeCandidates: "Purge candidates",
};

/**
 * The audit approval card: server-computed findings rendered verbatim with checkboxes, and one
 * Approve action that mints a grant for exactly what was seen. This is the approve-what-you-saw
 * surface — the same card contract Plan mode's approval flow reuses.
 */
export function AuditCard({ kind, runAudit, onApprove, onClose, approvable = true, busy = false }: AuditCardProps) {
  const [result, setResult] = useState<RhinoAuditResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [approving, setApproving] = useState(false);
  const [checked, setChecked] = useState<Record<string, boolean>>({});
  const [keepFirst, setKeepFirst] = useState<Record<string, boolean>>({});

  useEffect(() => {
    let disposed = false;
    setLoading(true);
    setError(null);
    setResult(null);
    runAudit(kind)
      .then((payload) => {
        if (disposed) return;
        setResult(payload);
        // Nothing is pre-checked: approving destructive work is an explicit act per finding.
        setChecked({});
        setKeepFirst({});
        setLoading(false);
      })
      .catch((cause) => {
        if (disposed) return;
        setError(cause instanceof Error ? cause.message : String(cause));
        setLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, [kind, runAudit]);

  const approved = useMemo(
    () => (result?.findings ?? []).filter((finding) => checked[finding.findingId]),
    [result, checked],
  );

  return (
    <section className="audit-card" aria-label={`${KIND_TITLES[kind]} audit`}>
      <header className="audit-card-header">
        <h3>
          {KIND_TITLES[kind]}
          {result ? (
            <span className="audit-card-meta">
              {" "}
              tolerance {result.toleranceUsed} {result.docUnits}
              {result.truncated ? " · partial scan" : ""}
            </span>
          ) : null}
        </h3>
        <button type="button" className="secondary-button" onClick={onClose} title="Close">
          Close
        </button>
      </header>
      {loading ? <p className="archive-note">Auditing the document…</p> : null}
      {error ? <p className="archive-error">{error}</p> : null}
      {result && !loading ? (
        result.findings.length === 0 ? (
          <p className="archive-note">
            No findings — {result.scannedObjects} object{result.scannedObjects === 1 ? "" : "s"} scanned clean.
          </p>
        ) : (
          <>
            <ul className="audit-card-list">
              {result.findings.map((finding) => (
                <li key={finding.findingId}>
                  <label>
                    {approvable ? (
                      <input
                        type="checkbox"
                        checked={checked[finding.findingId] ?? false}
                        onChange={(event) =>
                          setChecked((current) => ({ ...current, [finding.findingId]: event.target.checked }))
                        }
                      />
                    ) : null}
                    <span className="audit-card-detail">{finding.detail}</span>
                  </label>
                  {finding.kind === "nearDuplicates" && (checked[finding.findingId] ?? false) ? (
                    <div className="audit-card-keep" role="radiogroup" aria-label="Which copy to keep">
                      {[true, false].map((first) => (
                        <label
                          key={String(first)}
                          title={first ? finding.objectIds[0] : finding.objectIds[1]}
                        >
                          <input
                            type="radio"
                            name={`keep-${finding.findingId}`}
                            checked={(keepFirst[finding.findingId] ?? true) === first}
                            onChange={() =>
                              setKeepFirst((current) => ({ ...current, [finding.findingId]: first }))
                            }
                          />
                          keep {first ? finding.objectIds[0]?.slice(0, 8) : finding.objectIds[1]?.slice(0, 8)}
                        </label>
                      ))}
                    </div>
                  ) : null}
                </li>
              ))}
            </ul>
            {approvable ? (
              <footer className="audit-card-footer">
                <button
                  type="button"
                  className="secondary-button"
                  disabled={approved.length === 0 || approving || busy}
                  title={busy ? "The curator session is busy or paused" : undefined}
                  onClick={() => {
                    setApproving(true);
                    setError(null);
                    // The card closes ONLY on success; a failed mint/send keeps it open with the
                    // error visible — silently losing an approval is worse than asking again.
                    onApprove(result, approved, keepFirst)
                      .then(() => onClose())
                      .catch((cause) => {
                        setError(cause instanceof Error ? cause.message : String(cause));
                      })
                      .finally(() => setApproving(false));
                  }}
                >
                  {approving ? "Approving…" : `Approve ${approved.length} finding${approved.length === 1 ? "" : "s"}`}
                </button>
                <span className="audit-card-meta">
                  Approval covers exactly the fixes' write targets at their audited fingerprints;
                  document-table entries (unused blocks, empty layers) need no object approval.
                </span>
              </footer>
            ) : (
              <footer className="audit-card-footer">
                <span className="audit-card-meta">
                  Report-only for now — the typed fix operations for these findings ship in a later
                  phase.
                </span>
              </footer>
            )}
          </>
        )
      ) : null}
    </section>
  );
}
