import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** A magnifier over a page whose lines say nothing: the search found nothing. */
export function NothingFound(props: IllustrationProps) {
  return (
    <Drawing name="nothingFound" {...props}>
      <path d="M40 18 H98 L114 34 V102 H40 Z" />
      <path d="M98 18 V34 H114" />
      <Fine d="M52 48 H100" />
      <Fine d="M52 60 H92" />
      <Fine d="M52 72 H100" />
      <Fine d="M52 84 H76" />
      <Tint cx={114} cy={78} r={16} />
      <circle cx={114} cy={78} r={16} />
      <path d="M126 90 L142 106" />
      <Fine d="M106 72 q4 -6 10 -4" />
      <Ground />
    </Drawing>
  );
}
