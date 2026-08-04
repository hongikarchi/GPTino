// Focus-reference markers: GPTino's common conversational primitive for pointing at
// Rhino geometry from chat text. Agents write `[[focus:<guid>[,<guid>...]|<label>]]`
// inline; the panel renders a clickable chip that drives POST /focus (select/isolate +
// zoom). Parsing is deliberately panel-side: message content travels the wire verbatim,
// so no server or store schema is touched.
//
// Safety rules (all enforced here, mirroring the audit card's "no dead rows" principle):
// a marker only becomes a chip when EVERY id is a well-formed GUID (the server binds
// IReadOnlyList<Guid> and would 400 otherwise) and the id count is sane. Anything else
// renders as the original text so a malformed marker never hides content.

export interface TextSegment {
  kind: "text";
  text: string;
}

export interface FocusSegment {
  kind: "focus";
  objectIds: string[];
  label: string;
}

/**
 * An alternative the agent is proposing (a solution variant, a design option). Clicking it
 * asks the owner to show that variant — for structural work that means switching the
 * viewport preview to the alt's visualization. `altId` is opaque to the panel: the agent
 * and whatever renders the variant agree on its meaning.
 */
export interface AltSegment {
  kind: "alt";
  altId: string;
  label: string;
}

export type MessageSegment = TextSegment | FocusSegment | AltSegment;

const MARKER = /\[\[focus:([^\]|]+)\|([^\]|]*)\]\]/g;
const ALT_MARKER = /\[\[alt:([A-Za-z0-9._-]{1,64})\|([^\]|]*)\]\]/g;
const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const MAX_IDS = 200;
const MAX_LABEL = 120;

export function parseMessageSegments(content: string): MessageSegment[] {
  // Collect every valid marker of either kind first, then stitch text around them in
  // document order — that keeps the two syntaxes independent and order-agnostic.
  const hits: { start: number; end: number; segment: FocusSegment | AltSegment }[] = [];

  MARKER.lastIndex = 0;
  for (let match = MARKER.exec(content); match !== null; match = MARKER.exec(content)) {
    const ids = match[1].split(",").map((id) => id.trim()).filter((id) => id.length > 0);
    const valid = ids.length > 0 && ids.length <= MAX_IDS && ids.every((id) => GUID.test(id));
    if (!valid) continue; // malformed markers stay raw text — never a dead chip
    hits.push({
      start: match.index,
      end: match.index + match[0].length,
      segment: {
        kind: "focus",
        objectIds: ids,
        label: match[2].trim().slice(0, MAX_LABEL) || `${ids.length}개 객체`,
      },
    });
  }

  ALT_MARKER.lastIndex = 0;
  for (let match = ALT_MARKER.exec(content); match !== null; match = ALT_MARKER.exec(content)) {
    hits.push({
      start: match.index,
      end: match.index + match[0].length,
      segment: {
        kind: "alt",
        altId: match[1],
        label: match[2].trim().slice(0, MAX_LABEL) || match[1],
      },
    });
  }

  hits.sort((a, b) => a.start - b.start);

  const segments: MessageSegment[] = [];
  let cursor = 0;
  for (const hit of hits) {
    if (hit.start < cursor) continue; // overlapping matches: first one wins
    if (hit.start > cursor) {
      segments.push({ kind: "text", text: content.slice(cursor, hit.start) });
    }
    segments.push(hit.segment);
    cursor = hit.end;
  }
  if (cursor < content.length) {
    segments.push({ kind: "text", text: content.slice(cursor) });
  }
  return segments.length > 0 ? segments : [{ kind: "text", text: content }];
}
