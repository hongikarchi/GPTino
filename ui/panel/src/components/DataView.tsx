import { useEffect, useState } from "react";
import type { DataFlowDetail, DocDataFlow, GrasshopperDocInfo } from "../types";

interface DataViewProps {
  /** Registered GH docs; null on a legacy single-doc server. */
  docs: GrasshopperDocInfo[] | null;
  /** Per-doc summaries pushed with the runtime snapshot (counts only). */
  summaries: DocDataFlow[];
  /** Stamped bakes whose source document predates provenance stamping. */
  unattributedBakeCount: number;
  rhinoFile: string;
  grasshopperFile: string;
  getDetail(docId?: string | null): Promise<DataFlowDetail>;
}

const shortId = (id: string) => id.slice(0, 8);
const shortFile = (path: string) => path.split(/[\\/]/).pop() ?? path;

/**
 * The Data tab: what Grasshopper reads from the Rhino document and what it writes back.
 * Summaries ride the runtime snapshot (never a second source of truth); the per-parameter and
 * per-family detail is fetched on demand and stamped with the revision it was observed at.
 */
export function DataView({
  docs,
  summaries,
  unattributedBakeCount,
  rhinoFile,
  grasshopperFile,
  getDetail,
}: DataViewProps) {
  const [selectedDocId, setSelectedDocId] = useState<string | null>(docs?.[0]?.id ?? null);
  const [detail, setDetail] = useState<DataFlowDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);

  // Follow the doc set: a Save As or a closed document must not leave the view pointed at a
  // document the runtime no longer knows.
  useEffect(() => {
    if (docs == null) return;
    if (selectedDocId == null || !docs.some((doc) => doc.id === selectedDocId)) {
      setSelectedDocId(docs[0]?.id ?? null);
    }
  }, [docs, selectedDocId]);

  useEffect(() => {
    let disposed = false;
    setLoading(true);
    setError(null);
    getDetail(selectedDocId)
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
  }, [selectedDocId, getDetail, reloadKey]);

  const references = detail?.references;
  const bakes = detail?.bakes;
  const ownGroups = (bakes?.groups ?? []).filter(
    (group) =>
      group.sourceDocKey != null &&
      detail?.docId != null &&
      group.sourceDocKey.toLowerCase() === detail.docId.toLowerCase(),
  );
  const foreign = (bakes?.groups ?? [])
    .filter(
      (group) =>
        group.sourceDocKey != null &&
        (detail?.docId == null || group.sourceDocKey.toLowerCase() !== detail.docId.toLowerCase()),
    )
    .reduce((total, group) => total + group.count, 0);
  const brokenTotal = summaries.reduce((total, flow) => total + flow.missingReferenceCount, 0);

  return (
    <div className="data-view">
      <header className="data-view-header">
        <div className="data-view-flow" aria-label="Document data flow">
          <span className="data-view-doc">{shortFile(rhinoFile)}</span>
          <span className="data-view-arrow" title="Rhino objects referenced by Grasshopper">⇢</span>
          <span className="data-view-doc">
            {docs?.find((doc) => doc.id === selectedDocId)?.file
              ? shortFile(docs.find((doc) => doc.id === selectedDocId)!.file)
              : shortFile(grasshopperFile)}
          </span>
          <span className="data-view-arrow" title="Objects baked back into Rhino">⇠</span>
        </div>
        <div className="data-view-actions">
          {detail?.revision != null ? (
            <span className="audit-card-meta">as of r{detail.revision}</span>
          ) : null}
          <button
            type="button"
            className="secondary-button"
            onClick={() => setReloadKey((key) => key + 1)}
            disabled={loading}
          >
            Rescan
          </button>
        </div>
      </header>

      {docs != null && docs.length > 1 ? (
        <div className="data-view-docs" role="tablist" aria-label="Grasshopper documents">
          {docs.map((doc) => {
            const summary = summaries.find((flow) => flow.docId === doc.id);
            return (
              <button
                key={doc.id}
                type="button"
                role="tab"
                aria-selected={doc.id === selectedDocId}
                className={`secondary-button${doc.id === selectedDocId ? " active" : ""}`}
                onClick={() => setSelectedDocId(doc.id)}
              >
                {shortFile(doc.file)}
                {summary ? (
                  <span className={summary.missingReferenceCount > 0 ? "data-view-chip warning" : "data-view-chip"}>
                    ⇢{summary.referenceCount} ⇠{summary.bakeCount}
                    {summary.missingReferenceCount > 0 ? ` · ${summary.missingReferenceCount}!` : ""}
                  </span>
                ) : null}
              </button>
            );
          })}
        </div>
      ) : null}

      {brokenTotal > 0 ? (
        <p className="data-view-alert">
          {brokenTotal} broken reference{brokenTotal === 1 ? "" : "s"}: a definition points at Rhino
          objects that no longer exist, so those components emit empty data with no error.
        </p>
      ) : null}

      <div className="data-view-body">
        {loading ? <p className="archive-note">Reading references and bakes…</p> : null}
        {error ? <p className="archive-error">{error}</p> : null}
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
                        <li key={object.rhinoObjectId} className={object.exists ? undefined : "data-drawer-missing"}>
                          <code>{shortId(object.rhinoObjectId)}</code>{" "}
                          {object.exists ? object.layerFullPath ?? "" : "missing — referenced object was deleted"}
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
              Bakes{" "}
              <span className="data-drawer-count">
                ⇠{ownGroups.reduce((total, group) => total + group.count, 0)}
              </span>
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
            {unattributedBakeCount > 0 ? (
              <p className="archive-note">
                {unattributedBakeCount} tracked bake{unattributedBakeCount === 1 ? "" : "s"} predate
                provenance stamping (unattributed — re-bake to attribute).
              </p>
            ) : null}
          </section>
        ) : null}
      </div>
    </div>
  );
}
