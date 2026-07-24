import type { ReactNode, SVGProps } from "react";

export type IconName =
  | "activity"
  | "arrowDown"
  | "arrowUp"
  | "chevron"
  | "drag"
  | "expand"
  | "history"
  | "paperclip"
  | "pause"
  | "play"
  | "send"
  | "stop"
  | "terminal"
  | "warning";

const paths: Record<IconName, ReactNode> = {
  activity: <path d="M3 12h3l2.2-6 3.6 12 2.4-7H21" />,
  arrowDown: <path d="m7 10 5 5 5-5" />,
  arrowUp: <path d="m7 14 5-5 5 5" />,
  chevron: <path d="m9 18 6-6-6-6" />,
  drag: (
    <>
      <circle cx="9" cy="6" r="1" fill="currentColor" stroke="none" />
      <circle cx="15" cy="6" r="1" fill="currentColor" stroke="none" />
      <circle cx="9" cy="12" r="1" fill="currentColor" stroke="none" />
      <circle cx="15" cy="12" r="1" fill="currentColor" stroke="none" />
      <circle cx="9" cy="18" r="1" fill="currentColor" stroke="none" />
      <circle cx="15" cy="18" r="1" fill="currentColor" stroke="none" />
    </>
  ),
  expand: <path d="M8 3H3v5m13-5h5v5M8 21H3v-5m18 0v5h-5" />,
  history: (
    <>
      <path d="M3 12a9 9 0 1 0 2.6-6.3L3 8" />
      <path d="M3 3v5h5" />
      <path d="M12 7v5l3.5 2" />
    </>
  ),
  paperclip: (
    <path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l8.57-8.57A4 4 0 1 1 18 8.84l-8.59 8.57a2 2 0 0 1-2.83-2.83l8.49-8.48" />
  ),
  pause: (
    <>
      <path d="M8 5v14" />
      <path d="M16 5v14" />
    </>
  ),
  play: <path d="m8 5 11 7-11 7Z" />,
  send: <path d="m4 4 16 8-16 8 3-8Zm3 8h13" />,
  stop: <rect x="6" y="6" width="12" height="12" rx="1" />,
  terminal: (
    <>
      <path d="m5 7 4 4-4 4" />
      <path d="M11 17h8" />
    </>
  ),
  warning: (
    <>
      <path d="M12 3 2.8 20h18.4Z" />
      <path d="M12 9v4" />
      <path d="M12 17h.01" />
    </>
  ),
};

export function Icon({ name, ...props }: { name: IconName } & SVGProps<SVGSVGElement>) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      {...props}
    >
      {paths[name]}
    </svg>
  );
}
