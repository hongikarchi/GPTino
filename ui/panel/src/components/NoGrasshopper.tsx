interface NoGrasshopperProps {
  /** What this tab would show once a definition is open. */
  detail: string;
  /** True when the curator session exists, so the offer to switch to it is real. */
  curatorAvailable: boolean;
  onOpenCurator(): void;
}

/**
 * Shown on the Model and Data tabs while no Grasshopper definition is open. Those two tabs are the
 * only ones that need a canvas — the curator works on the Rhino document alone — so this replaces
 * the tab body instead of gating the whole panel.
 *
 * The button navigates to the gptino: scheme, which the Rhino-side WebView intercepts and turns
 * into the _Grasshopper command. There is no HTTP request behind it.
 */
export function NoGrasshopper({ detail, curatorAvailable, onOpenCurator }: NoGrasshopperProps) {
  return (
    <div className="tab-empty">
      <div className="tab-empty-body">
        <strong>No Grasshopper definition is open</strong>
        <p>{detail}</p>
        <div className="tab-empty-actions">
          <a className="new-session-button" href="gptino://open-grasshopper">
            Open Grasshopper
          </a>
          {curatorAvailable ? (
            <button type="button" className="secondary-button" onClick={onOpenCurator}>
              Go to Curator
            </button>
          ) : null}
        </div>
        <p className="tab-empty-note">
          The Curator tab needs no definition — it works on the Rhino document directly.
        </p>
      </div>
    </div>
  );
}
