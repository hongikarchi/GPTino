interface CuratorActionsProps {
  /** True while the curator session is running a turn — presets disable instead of queueing. */
  busy: boolean;
  onRun(prompt: string): void;
}

/**
 * The curator tab's preset row ("stream deck"): each button is a pre-composed curator turn — it
 * rides the normal session/broker path, never a bypass. Destructive work still goes through the
 * audit-first protocol the curator role encodes.
 */
const PRESETS: { label: string; title: string; prompt: string }[] = [
  {
    label: "Check-up",
    title: "Full document check-up (all audits + data ledger)",
    prompt:
      "Run a full document check-up: rhino_audit for nearMissEndpoints, nearDuplicates, and " +
      "purgeCandidates, plus data_flow_read. Summarize every finding with its measure, tolerance, " +
      "and units; propose fixes but change nothing yet.",
  },
  {
    label: "Endpoint gaps",
    title: "Open-curve endpoints that almost meet",
    prompt:
      "Run rhino_audit kind=nearMissEndpoints and report each near-miss pair with its gap, " +
      "tolerance, and units. Propose a fix per pair; change nothing yet.",
  },
  {
    label: "Duplicates",
    title: "Near-duplicate curves/points SelDup cannot catch",
    prompt:
      "Run rhino_audit kind=nearDuplicates and report each candidate pair with its deviation. " +
      "Remind me that which copy to keep is my decision; change nothing yet.",
  },
  {
    label: "Purge candidates",
    title: "Unused block definitions, empty layers, invalid objects",
    prompt:
      "Run rhino_audit kind=purgeCandidates and report unused block definitions, empty leaf " +
      "layers, and invalid objects (quarantine candidates). Change nothing yet.",
  },
  {
    label: "Data ledger",
    title: "What Grasshopper references and bakes",
    prompt:
      "Run data_flow_read and report what this definition references from the Rhino document " +
      "(broken references prominently) and what it has baked. Change nothing.",
  },
];

export function CuratorActions({ busy, onRun }: CuratorActionsProps) {
  return (
    <div className="curator-actions" role="toolbar" aria-label="Document care presets">
      {PRESETS.map((preset) => (
        <button
          key={preset.label}
          type="button"
          className="secondary-button"
          title={preset.title}
          disabled={busy}
          onClick={() => onRun(preset.prompt)}
        >
          {preset.label}
        </button>
      ))}
    </div>
  );
}
