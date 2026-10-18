import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** An open carton, flaps up, nothing inside; a tag still hangs from one flap. */
export function NothingYet(props: IllustrationProps) {
  return (
    <Drawing name="nothingYet" {...props}>
      <Tint d="M56 52 H104 V64 H56 Z" />
      <path d="M44 64 H116 V104 H44 Z" />
      <path d="M44 64 L56 52 H104 L116 64" />
      <path d="M44 64 L34 42 L60 38 L56 52" />
      <path d="M116 64 L126 42 L100 38 L104 52" />
      <Fine d="M80 64 V104" />
      <path d="M124 72 L132 64 L140 72 L132 80 Z" />
      <Fine d="M132 64 L128 56" />
      <Ground />
    </Drawing>
  );
}
