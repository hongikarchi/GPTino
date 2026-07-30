import { useEffect, useState } from "react";
import type { DataFlowDetail } from "../types";

interface DataFlowDrawerProps {
  /** GH docKey to inspect; null = the only registered doc (legacy single-doc server). */
  docId: string | null;
  docName?: string;
  onClose(): void;
  getDetail(docId?: string | null): Promise<DataFlowDetail>;
}

const shortId = (id: string) => id.slice(0, 8);

/**
 * Bottom drawer showing the on-demand Rhino<->GH data-flow detail: per-parameter references
 * (missing = broken references silently emitting empty data) and the stamped-bake census.
 * Every payload carries the revision it was observed at — the drawer displays it and never
 * pretends to be fresher than the read that produced it.
 */
export function DataFlowDrawer({ docId, docName, onClose, getDetail }: DataFlowDrawerProps) {
  const [detail, setDetail] = useState<DataFlowDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      // Full-screen overlays (archive, deleted sessions) stack above this drawer and own the
      // Escape while open — without this guard one keypress would close both layers.
      if (document.querySelector(".archive-overlay")) return;
      onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  useEffect(() => {
    let disposed = false;
    setLoading(true);
    setError(null);
    getDetail(docId)
      .then((payload) => {
        if (disposed) return;
        setDetail(payload);
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
  }, [docId, getDetail, reloadKey]);

  const references = detail?.references;
  const bakes = detail?.bakes;
  const ownGroups = (bakes?.groups ?? []).filter(
    (group) =>
      group.sourceDocKey != null &&
      detail?.docId != null &&
      group.sourceDocKey.toLowerCase() === detail.docId.toLowerCase(),
  );
  const unattributed = (bakes?.groups ?? [])
    .filter((group) => group.sourceDocKey == null)
    .reduce((total, group) => total + group.count, 0);
  // Stamps keyed to a different (or re-keyed) definition: another open doc's bakes, or orphans
  // after Save As recomputed the docKey. Surfaced honestly instead of silently dropped.
  const foreign = (bakes?.groups ?? [])
    .filter(
      (group) =>
        group.sourceDocKey != null &&
        (detail?.docId == null || group.sourceDocKey.toLowerCase() !== detail.docId.toLowerCase()),
    )
    .reduce((total, group) => total + group.count, 0);

  return (
    <div className="data-drawer" role="dialog" aria-label="Rhino and Grasshopper data flow">
      <header className="archive-header">
        <h2 className="archive-title">
          Data flow — {docName ?? detail?.docId ?? "Grasshopper"}
          {detail?.revision != null ? <span className="data-drawer-stamp"> as of r{detail.revision}</span> : null}
        </h2>
        <div className="data-drawer-actions">
          <button
            type="button"
            className="secondary-button"
            onClick={() => setReloadKey((key) => key + 1)}
            disabled={loading}
          >
            Rescan
          </button>
          <button type="button" className="secondary-button" onClick={onClose} title="Close (Esc)">
            Close
          </button>
        </div>
      </header>
      <div className="data-drawer-body">
        {loading ? <p className="archive-note">Reading references and bakes…</p> : null}
        {error ? (
          <div className="archive-error">
            <p>{error}</p>
            <button type="button" className="secondary-button" onClick={() => setReloadKey((key) => key + 1)}>
              Retry
            </button>
          </div>
        ) : null}
        {!loading && !error && detail?.writerActive ? (
          <p className="archive-note">{detail.message ?? "A writer session holds the document; retry shortly."}</p>
        ) : null}
        {!loading && !error && references ? (
          <section className="data-drawer-section">
            <h3>
              References <span className="data-drawer-count">⇢{references.referenceCount}</span>
              {references.missingCount > 0 ? (
                <span className="data-drawer-count warning"> · {references.missingCount} broken</span>
              ) : null}
            </h3>
            {references.parameters.length === 0 ? (
              <p className="archive-note">This definition references no Rhino objects.</p>
            ) : (
              <ul className="data-drawer-list">
                {references.parameters.map((parameter) => (
                  <li key={parameter.parameterId}>
                    <span className="data-drawer-param">
                      {parameter.nickName || parameter.parameterType}
                      <span className="data-drawer-type"> {parameter.parameterType}</span>
                    </span>
                    <ul>
                      {parameter.objects.map((object) => (
                        <li
                          key={object.rhinoObjectId}
                          className={object.exists ? undefined : "data-drawer-missing"}
                        >
                          <code>{shortId(object.rhinoObjectId)}</code>{" "}
                          {object.exists
                            ? object.layerFullPath ?? ""
                            : "missing — referenced object was deleted"}
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            )}
          </section>
        ) : null}
        {!loading && !error && bakes ? (
          <section className="data-drawer-section">
            <h3>
              Bakes <span className="data-drawer-count">⇠{ownGroups.reduce((total, group) => total + group.count, 0)}</span>
            </h3>
            {ownGroups.length === 0 ? (
              <p className="archive-note">No stamped bakes from this definition yet.</p>
            ) : (
              <ul className="data-drawer-list">
                {ownGroups.map((group) => (
                  <li key={`${group.sourceDocKey}|${group.bakeFamily}`}>
                    <span className="data-drawer-param">{group.bakeFamily ?? "(no family)"}</span>{" "}
                    {group.count} object{group.count === 1 ? "" : "s"}
                  </li>
                ))}
              </ul>
            )}
            {foreign > 0 ? (
              <p className="archive-note">
                {foreign} tracked bake{foreign === 1 ? "" : "s"} belong to other or re-keyed
                definitions (a Save As re-keys the document; re-bake to re-attribute).
              </p>
            ) : null}
            {unattributed > 0 ? (
              <p className="archive-note">
                {unattributed} tracked bake{unattributed === 1 ? "" : "s"} predate provenance stamping
                (unattributed — re-bake to attribute).
              </p>
            ) : null}
          </section>
        ) : null}
      </div>
    </div>
  );
}
