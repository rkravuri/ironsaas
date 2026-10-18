import { Drawing, Figure, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** Someone reading a map at a signpost whose arrows disagree: the page is not where the address says. */
export function Lost(props: IllustrationProps) {
  return (
    <Drawing name="lost" {...props}>
      <path d="M108 36 V106" />
      <path d="M100 42 H126 L132 48 L126 54 H100 Z" />
      <path d="M116 62 H90 L84 68 L90 74 H116 Z" />
      <Figure x={52} y={48} pose="reading" />
      <Tint d="M38 72 L52 68 L66 72 L66 90 L52 86 L38 90 Z" />
      <path d="M38 72 L52 68 L66 72 L66 90 L52 86 L38 90 Z" />
      <Fine d="M52 68 V86" />
      <Fine d="M43 80 q5 2 9 -1" />
      <Ground />
    </Drawing>
  );
}
