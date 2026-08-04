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

export type MessageSegment = TextSegment | FocusSegment;

const MARKER = /\[\[focus:([^\]|]+)\|([^\]|]*)\]\]/g;
const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const MAX_IDS = 200;
const MAX_LABEL = 120;

export function parseMessageSegments(content: string): MessageSegment[] {
  const segments: MessageSegment[] = [];
  let cursor = 0;
  MARKER.lastIndex = 0;
  for (let match = MARKER.exec(content); match !== null; match = MARKER.exec(content)) {
    const ids = match[1].split(",").map((id) => id.trim()).filter((id) => id.length > 0);
    const valid = ids.length > 0 && ids.length <= MAX_IDS && ids.every((id) => GUID.test(id));
    if (!valid) continue; // leave the raw text in place via the trailing text segment
    if (match.index > cursor) {
      segments.push({ kind: "text", text: content.slice(cursor, match.index) });
    }
    const label = match[2].trim().slice(0, MAX_LABEL) || `${ids.length}개 객체`;
    segments.push({ kind: "focus", objectIds: ids, label });
    cursor = match.index + match[0].length;
  }
  if (cursor < content.length) {
    segments.push({ kind: "text", text: content.slice(cursor) });
  }
  return segments.length > 0 ? segments : [{ kind: "text", text: content }];
}
