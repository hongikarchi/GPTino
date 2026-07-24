// Thin wrapper around the browser Notification API. The panel can run inside a
// Rhino WebView where `Notification` may be missing or throw, so every access is
// feature-detected and the permission request is never fired unconditionally.

export function notificationsSupported(): boolean {
  return typeof window !== "undefined" && "Notification" in window;
}

/**
 * Returns the current permission if it is already resolved (granted/denied) and
 * otherwise prompts once. The browser remembers a "denied" answer, so this never
 * re-prompts a user who declined. Resolves to "unsupported" when the API is absent
 * or throws (embedded WebView hosts).
 */
export async function ensureNotificationPermission(): Promise<NotificationPermission | "unsupported"> {
  if (!notificationsSupported()) return "unsupported";
  try {
    const current = Notification.permission;
    if (current === "granted" || current === "denied") return current;
    return await Notification.requestPermission();
  } catch {
    return "unsupported";
  }
}
