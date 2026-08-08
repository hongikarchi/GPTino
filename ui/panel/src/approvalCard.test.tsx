import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { createDemoRuntimeState } from "./api/mock";
import { ApprovalCard } from "./components/ApprovalCard";
import { approvalTargetRows } from "./components/approvalTargets";
import type { ApprovalCard as ApprovalCardData, ApprovalItem, CanvasFocusResult } from "./types";

const GUID_A = "a0b1c2d3-0004-4e4e-9f9f-000000000004";
const GUID_B = "a0b1c2d3-0005-4e4e-9f9f-000000000005";

const cleanupItem: ApprovalItem = {
  id: "orphan-1",
  label: "쓰이지 않는 파이프라인을 삭제합니다",
  targets: [
    {
      objectId: GUID_A,
      fingerprint: "fp-1",
      label: "Series (GridX-old)",
      role: "예전 X 그리드 간격을 만들던 시리즈",
      impact: "삭제해도 결과 기하는 변하지 않습니다",
    },
    // Authored label only — role/impact lines must be omitted, but the row (and zoom) still shows.
    { objectId: GUID_B, fingerprint: "fp-2", label: "Unit X (old)" },
  ],
};

const legacyItem: ApprovalItem = {
  id: "dup-1",
  label: "커브 2개가 겹칩니다",
  targets: [{ objectId: "a0b1c2d3-0001-4e4e-9f9f-000000000001", fingerprint: "fp-a1" }],
};

function cardWith(items: ApprovalItem[]): ApprovalCardData {
  return { status: "proposing", summary: "요약", items };
}

const focusCanvasStub = (objectIds: string[]): Promise<CanvasFocusResult> =>
  Promise.resolve({ selectedCount: objectIds.length, missingCount: 0, fingerprint: "test" });

describe("approval target rows (destructive-cleanup context)", () => {
  it("projects one row per authored target, each zooming exactly its own objectId", () => {
    const rows = approvalTargetRows(cleanupItem);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({
      heading: "Series (GridX-old)",
      role: "예전 X 그리드 간격을 만들던 시리즈",
      impact: "삭제해도 결과 기하는 변하지 않습니다",
      // The payload handed to the existing canvas-focus channel (POST /canvas/focus) on zoom.
      zoomObjectIds: [GUID_A],
    });
    expect(rows[1].role).toBeUndefined();
    expect(rows[1].impact).toBeUndefined();
    expect(rows[1].zoomObjectIds).toEqual([GUID_B]);
  });

  it("projects nothing for legacy bare (objectId, fingerprint) targets", () => {
    expect(approvalTargetRows(legacyItem)).toEqual([]);
  });

  it("keeps row keys unique when one item repeats an objectId", () => {
    const duplicated: ApprovalItem = {
      id: "dup-target",
      label: "같은 컴포넌트가 두 번 등장",
      targets: [
        { objectId: GUID_A, fingerprint: "fp-1", label: "First mention" },
        { objectId: GUID_A, fingerprint: "fp-1", label: "Second mention" },
        { objectId: GUID_B, fingerprint: "fp-2", label: "Other" },
      ],
    };
    const rows = approvalTargetRows(duplicated);
    expect(rows).toHaveLength(3);
    const keys = rows.map((row) => row.key);
    // All keys unique; the first occurrence keeps the legacy bare key so well-formed
    // cards render with unchanged identity.
    expect(new Set(keys).size).toBe(3);
    expect(keys[0]).toBe(`dup-target:${GUID_A}`);
    expect(keys[1]).toBe(`dup-target:${GUID_A}#1`);
    expect(keys[2]).toBe(`dup-target:${GUID_B}`);
  });
});

describe("ApprovalCard rendering", () => {
  it("renders label, 역할/변경 lines, and a GH canvas zoom chip per authored target", () => {
    const html = renderToStaticMarkup(
      <ApprovalCard card={cardWith([cleanupItem])} onAnswer={() => {}} onFocusCanvas={focusCanvasStub} />,
    );
    expect(html).toContain("Series (GridX-old)");
    expect(html).toContain("역할: 예전 X 그리드 간격을 만들던 시리즈");
    expect(html).toContain("변경: 삭제해도 결과 기하는 변하지 않습니다");
    // The zoom chip is the existing ghfocus mechanism (GhFocusChip -> onFocusCanvas), one per target.
    expect(html.match(/focus-chip gh/g)).toHaveLength(2);
    expect(html).toContain("확대");
    // Label-only target renders its heading without role/impact lines.
    expect(html).toContain("Unit X (old)");
    expect(html.match(/역할:/g)).toHaveLength(1);
    expect(html.match(/변경:/g)).toHaveLength(1);
  });

  it("renders legacy cards exactly without target rows, and no chips without a canvas channel", () => {
    const legacyHtml = renderToStaticMarkup(<ApprovalCard card={cardWith([legacyItem])} onAnswer={() => {}} onFocusCanvas={focusCanvasStub} />);
    expect(legacyHtml).not.toContain("approval-card-targets");
    expect(legacyHtml).not.toContain("역할:");
    expect(legacyHtml).not.toContain("변경:");

    const noChannelHtml = renderToStaticMarkup(<ApprovalCard card={cardWith([cleanupItem])} onAnswer={() => {}} />);
    expect(noChannelHtml).toContain("역할:");
    expect(noChannelHtml).not.toContain("focus-chip gh");
  });
});

describe("demo fixture", () => {
  it("ships an approval card with zoom-able role/impact targets for ?demo=1 verification", () => {
    const state = createDemoRuntimeState();
    const withCard = state.sessions.find((session) => session.approvalCard != null);
    expect(withCard).toBeDefined();
    const card = JSON.parse(withCard!.approvalCard!) as ApprovalCardData;
    const authoredTargets = card.items.flatMap((item) => item.targets).filter((target) => target.role && target.impact);
    expect(authoredTargets.length).toBeGreaterThan(0);
    for (const target of authoredTargets) {
      expect(target.objectId).toMatch(/^[0-9a-f-]{36}$/);
      expect(target.fingerprint!.length).toBeGreaterThan(0);
      expect(target.label!.length).toBeGreaterThan(0);
    }
    // Legacy items must coexist so the omission path stays demo-verifiable on the same card.
    expect(card.items.some((item) => item.targets.every((target) => !target.role && !target.impact))).toBe(true);
  });
});
