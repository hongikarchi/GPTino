import type { RhinoAuditKind } from "../types";

interface CuratorActionsProps {
  /** True while the curator session is running a turn — presets disable instead of queueing. */
  busy: boolean;
  onRun(prompt: string): void;
  /** Audit presets open the approval card (server-computed findings + Approve) instead of a turn. */
  onAudit(kind: RhinoAuditKind): void;
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
  // The three audit presets are card-first (see AUDIT_PRESETS): server-computed findings render
  // with checkboxes, and Approve mints the grant — a chat turn only happens after approval.
  {
    label: "Data ledger",
    title: "What Grasshopper references and bakes",
    prompt:
      "Run data_flow_read and report what this definition references from the Rhino document " +
      "(broken references prominently) and what it has baked. Change nothing.",
  },
  {
    label: "Layers",
    title: "Layer table: paths, counts, visibility, saved states",
    prompt:
      "Run rhino_layers and report the layer table: full paths, object counts (including hidden " +
      "and block members), visibility/lock, which layers are empty leaves, and the saved named " +
      "layer states. Change nothing.",
  },
  {
    label: "Layer checkpoint",
    title: "Save a named layer state before reorganizing",
    prompt:
      "Save a named layer state called 'GPTino checkpoint' with one saveRhinoLayerState operation " +
      "(action save), so any later layer work can be reverted without touching geometry. Report " +
      "the committed job state and the resulting list of saved states.",
  },
];

const AUDIT_PRESETS: { label: string; title: string; kind: RhinoAuditKind }[] = [
  { label: "Endpoint gaps", title: "Open-curve endpoints that almost meet", kind: "nearMissEndpoints" },
  { label: "Duplicates", title: "Near-duplicate curves/points SelDup cannot catch", kind: "nearDuplicates" },
  { label: "Purge candidates", title: "Unused blocks, empty layers, invalid objects", kind: "purgeCandidates" },
];

export function CuratorActions({ busy, onRun, onAudit }: CuratorActionsProps) {
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
      {AUDIT_PRESETS.map((preset) => (
        <button
          key={preset.label}
          type="button"
          className="secondary-button"
          title={preset.title}
          disabled={busy}
          onClick={() => onAudit(preset.kind)}
        >
          {preset.label}
        </button>
      ))}
    </div>
  );
}
