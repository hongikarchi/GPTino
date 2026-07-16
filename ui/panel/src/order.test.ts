import { describe, expect, it } from "vitest";
import { moveById, shiftById } from "./order";

const items = [{ id: "a" }, { id: "b" }, { id: "c" }];

describe("session ordering", () => {
  it("moves a session to the requested position", () => {
    expect(moveById(items, "c", "a").map(({ id }) => id)).toEqual(["c", "a", "b"]);
  });

  it("supports keyboard movement and preserves bounds", () => {
    expect(shiftById(items, "b", -1).map(({ id }) => id)).toEqual(["b", "a", "c"]);
    expect(shiftById(items, "a", -1).map(({ id }) => id)).toEqual(["a", "b", "c"]);
  });
});
