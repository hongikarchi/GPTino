export function moveById<T extends { id: string }>(
  items: readonly T[],
  sourceId: string,
  targetId: string,
): T[] {
  const from = items.findIndex((item) => item.id === sourceId);
  const to = items.findIndex((item) => item.id === targetId);

  if (from < 0 || to < 0 || from === to) {
    return [...items];
  }

  const next = [...items];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  return next;
}

export function shiftById<T extends { id: string }>(
  items: readonly T[],
  id: string,
  direction: -1 | 1,
): T[] {
  const from = items.findIndex((item) => item.id === id);
  const to = from + direction;

  if (from < 0 || to < 0 || to >= items.length) {
    return [...items];
  }

  return moveById(items, id, items[to].id);
}
