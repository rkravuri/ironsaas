import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** Someone with a map at a signpost whose arrows disagree: the page is not where the address says. */
export function Lost(props: IllustrationProps) {
  return (
    <Drawing name="lost" {...props}>
      <path d="M108 36 V106" />
      <Tint d="M100 42 H126 L132 48 L126 54 H100 Z" />
      <path d="M100 42 H126 L132 48 L126 54 H100 Z" />
      <path d="M116 62 H90 L84 68 L90 74 H116 Z" />
      <circle cx={52} cy={40} r={8} />
      <path d="M52 48 V78" />
      <path d="M52 78 L44 106" />
      <path d="M52 78 L60 106" />
      <path d="M52 58 L38 66" />
      <path d="M52 58 L66 66" />
      <Tint d="M32 64 L48 58 L66 64 L66 84 L48 78 L32 84 Z" />
      <path d="M32 64 L48 58 L66 64 L66 84 L48 78 L32 84 Z" />
      <Fine d="M48 58 V78" />
      <Fine d="M38 72 q5 2 10 -1" />
      <Ground />
    </Drawing>
  );
}
