interface NoGrasshopperProps {
  /** What this tab would show once a definition is open. */
  detail: string;
}

/**
 * Shown on the Model and Data tabs while no Grasshopper definition is open. Those two tabs are the
 * only ones that need a canvas — Rhino-side work needs none — so this replaces the tab body instead
 * of gating the whole panel.
 *
 * The button navigates to the gptino: scheme, which the Rhino-side WebView intercepts and turns
 * into the _Grasshopper command. There is no HTTP request behind it.
 */
export function NoGrasshopper({ detail }: NoGrasshopperProps) {
  return (
    <div className="tab-empty">
      <div className="tab-empty-body">
        <strong>No Grasshopper definition is open</strong>
        <p>{detail}</p>
        <div className="tab-empty-actions">
          <a className="new-session-button" href="gptino://open-grasshopper">
            Open Grasshopper
          </a>
        </div>
      </div>
    </div>
  );
}
