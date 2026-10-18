import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** A tray of finished documents with a tick on top, and the cup that was earned. */
export function AllDone(props: IllustrationProps) {
  return (
    <Drawing name="allDone" {...props}>
      <Tint d="M34 84 H126 L120 106 H40 Z" />
      <path d="M34 84 H126 L120 106 H40 Z" />
      <path d="M52 38 H108 V84 H52 Z" />
      <path d="M58 30 H114 V38" />
      <path d="M64 22 H120 V30" />
      <Fine d="M62 50 H98" />
      <Fine d="M62 60 H90" />
      <path d="M66 70 L76 80 L98 56" />
      <path d="M132 76 H150 L147 96 H135 Z" />
      <path d="M150 80 a6 6 0 0 1 0 12" />
      <Fine d="M138 70 q3 -4 0 -8" />
      <Fine d="M144 70 q3 -4 0 -8" />
      <Ground />
    </Drawing>
  );
}
