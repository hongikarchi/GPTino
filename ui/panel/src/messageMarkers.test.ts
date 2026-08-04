import { describe, expect, it } from "vitest";
import { parseMessageSegments } from "./messageMarkers";

const A = "a0b1c2d3-0001-4e4e-9f9f-000000000001";
const B = "a0b1c2d3-0002-4e4e-9f9f-000000000002";

describe("parseMessageSegments", () => {
  it("returns the whole content as one text segment when no marker exists", () => {
    expect(parseMessageSegments("그냥 평범한 메시지")).toEqual([
      { kind: "text", text: "그냥 평범한 메시지" },
    ]);
  });

  it("splits text around a single marker", () => {
    const out = parseMessageSegments(`앞 [[focus:${A}|가새 끝점]] 뒤`);
    expect(out).toEqual([
      { kind: "text", text: "앞 " },
      { kind: "focus", objectIds: [A], label: "가새 끝점" },
      { kind: "text", text: " 뒤" },
    ]);
  });

  it("parses multiple markers with multiple ids", () => {
    const out = parseMessageSegments(`[[focus:${A},${B}|둘]] 사이 [[focus:${B}|하나]]`);
    expect(out.filter((s) => s.kind === "focus")).toHaveLength(2);
    expect(out[0]).toEqual({ kind: "focus", objectIds: [A, B], label: "둘" });
  });

  it("keeps malformed GUIDs as raw text instead of making a dead chip", () => {
    const raw = "본문 [[focus:not-a-guid|나쁨]] 끝";
    expect(parseMessageSegments(raw)).toEqual([{ kind: "text", text: raw }]);
  });

  it("defaults the label when omitted", () => {
    const out = parseMessageSegments(`[[focus:${A}|]]`);
    expect(out[0]).toEqual({ kind: "focus", objectIds: [A], label: "1개 객체" });
  });

  it("leaves unterminated brackets untouched", () => {
    const raw = `[[focus:${A}|열림`;
    expect(parseMessageSegments(raw)).toEqual([{ kind: "text", text: raw }]);
  });

  it("parses alt markers and keeps document order alongside focus markers", () => {
    const out = parseMessageSegments(`대안: [[alt:alt-upsize|단면 확대]] 또는 [[focus:${A}|여기]]`);
    expect(out[1]).toEqual({ kind: "alt", altId: "alt-upsize", label: "단면 확대" });
    expect(out[3]).toEqual({ kind: "focus", objectIds: [A], label: "여기" });
  });

  it("falls back to the alt id when the label is omitted", () => {
    expect(parseMessageSegments("[[alt:base|]]")[0]).toEqual({
      kind: "alt",
      altId: "base",
      label: "base",
    });
  });

  it("rejects alt ids with unsafe characters", () => {
    const raw = "[[alt:../etc|나쁨]]";
    expect(parseMessageSegments(raw)).toEqual([{ kind: "text", text: raw }]);
  });

  it("trims whitespace inside the id list", () => {
    const out = parseMessageSegments(`[[focus: ${A} , ${B} |쌍]]`);
    expect(out[0]).toEqual({ kind: "focus", objectIds: [A, B], label: "쌍" });
  });
});
