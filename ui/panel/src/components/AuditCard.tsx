import { useEffect, useMemo, useState } from "react";
import type {
  FocusMode,
  FocusResult,
  RhinoAuditFinding,
  RhinoAuditKind,
  RhinoAuditResult,
} from "../types";
import { useFocusTarget } from "./useFocusTarget";

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
  /**
   * Point the viewport at a finding's objects. Absent in contexts with no live document (the
   * demo runtime still provides it, so the control is exercised there too).
   */
  onFocus?(objectIds: string[], mode: FocusMode): Promise<FocusResult>;
  /** False renders report-only (no checkboxes/Approve) — for kinds whose fix ops ship later. */
  approvable?: boolean;
  /** True while the curator is busy/paused — Approve would mint a grant no turn could consume. */
  busy?: boolean;
}

const KIND_TITLES: Record<RhinoAuditKind, string> = {
  nearMissEndpoints: "Endpoint gaps",
  nearDuplicates: "Near-duplicates",
  openBrepEdges: "Open solids",
  geometryIntegrity: "Geometry QC",
  layerIntegrity: "Layer QC",
  blockIntegrity: "Block QC",
  purgeCandidates: "Purge candidates",
};

/** What each kind actually looks at, so "0 scanned" can name what was missing. */
const KIND_SCOPES: Record<RhinoAuditKind, string> = {
  nearMissEndpoints: "open curves",
  nearDuplicates: "points, curves or solids",
  openBrepEdges: "multi-face solids",
  geometryIntegrity: "objects",
  layerIntegrity: "layers",
  blockIntegrity: "block definitions",
  purgeCandidates: "document entries",
};

/** Where to look instead when a kind has nothing in scope. */
const KIND_ALTERNATIVES: Partial<Record<RhinoAuditKind, string>> = {
  nearMissEndpoints: "Open solids",
  nearDuplicates: "Open solids or Purge candidates",
  openBrepEdges: "Purge candidates",
};

/**
 * Kinds whose findings name Rhino OBJECTS. Layer- and block-level findings identify a layer or a
 * definition, which the viewport cannot zoom to.
 */
const FOCUSABLE_KINDS = new Set([
  "nearMissEndpoints",
  "nearDuplicates",
  "openBrepEdges",
  "badObject",
  "tinyObject",
  "sliverObject",
  "strayObject",
  "partialDuplicate",
  "adjacentFaceGap",
  "multipleMappingChannels",
  "noTextureMapping",
]);

/**
 * The audit approval card: server-computed findings rendered verbatim with checkboxes, and one
 * Approve action that mints a grant for exactly what was seen. This is the approve-what-you-saw
 * surface — the same card contract Plan mode's approval flow reuses.
 */
export function AuditCard({
  kind,
  runAudit,
  onApprove,
  onClose,
  onFocus,
  approvable = true,
  busy = false,
}: AuditCardProps) {
  const [result, setResult] = useState<RhinoAuditResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [approving, setApproving] = useState(false);
  const [checked, setChecked] = useState<Record<string, boolean>>({});
  const [keepFirst, setKeepFirst] = useState<Record<string, boolean>>({});
  // How "Show in Rhino" treats everything else. Kept per card, because a user inspecting a list of
  // findings wants the same treatment for each one they press. Everything else about focusing —
  // busy/result/isolation bookkeeping, phrasing, unmount restore — comes from the shared
  // useFocusTarget contract that chat focus chips also use.
  const [focusMode, setFocusMode] = useState<FocusMode>("select");
  const focusTarget = useFocusTarget(onFocus);
  const { restore, busyKey: focusBusy, isolating } = focusTarget;
  const focus = (findingId: string, objectIds: string[]) =>
    focusTarget.focus(findingId, objectIds, focusMode);
  const focusNote = (findingId: string) => focusTarget.notes[findingId] ?? "";

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

  // (Closing the card must not strand the document with everything else hidden — that cleanup
  // now lives in useFocusTarget, shared with the chat focus chips.)

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
        <div className="audit-card-actions">
          {onFocus ? (
            <>
              <label className="audit-card-focus-mode" title="What Show in Rhino does with everything else">
                <select value={focusMode} onChange={(event) => setFocusMode(event.target.value as FocusMode)}>
                  <option value="select">Select + zoom</option>
                  <option value="isolate">…and hide the rest</option>
                  <option value="lock">…and lock the rest</option>
                </select>
              </label>
              {isolating ? (
                <button
                  type="button"
                  className="secondary-button"
                  onClick={() => void restore()}
                  disabled={focusBusy === "restore"}
                  title="Show and unlock everything this card hid or locked"
                >
                  Restore view
                </button>
              ) : null}
            </>
          ) : null}
          <button type="button" className="secondary-button" onClick={onClose} title="Close">
            Close
          </button>
        </div>
      </header>
      {loading ? <p className="archive-note">Auditing the document…</p> : null}
      {error ? <p className="archive-error">{error}</p> : null}
      {result && !loading ? (
        result.findings.length === 0 ? (
          // Nothing scanned is NOT a clean document: this check looks at particular geometry, and a
          // solids- or block-heavy model can hold none of it. Saying "clean" there would be a false
          // all-clear — the one thing this project must never report.
          result.scannedObjects === 0 ? (
            <p className="audit-card-nothing-scanned">
              Nothing to check — this document holds no {KIND_SCOPES[kind]} at the top level, so this
              check looked at 0 objects. That is not the same as clean.
              {KIND_ALTERNATIVES[kind] ? ` Try ${KIND_ALTERNATIVES[kind]} instead.` : ""}
            </p>
          ) : (
            <p className="archive-note">
              No findings — {result.scannedObjects} {KIND_SCOPES[kind]} scanned clean.
            </p>
          )
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
                  {/* Layer and block findings name a layer/definition id, not an object id, so
                      there is nothing for the viewport to zoom to. Offering a dead button would be
                      worse than offering none. */}
                  {onFocus && FOCUSABLE_KINDS.has(finding.kind) ? (
                    <div className="audit-card-focus">
                      <button
                        type="button"
                        className="secondary-button"
                        disabled={focusBusy === finding.findingId}
                        onClick={() => void focus(finding.findingId, finding.objectIds)}
                        title="Select these objects and zoom the viewports to them"
                      >
                        Show in Rhino
                      </button>
                      <span className="audit-card-focus-note">{focusNote(finding.findingId)}</span>
                    </div>
                  ) : null}
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
