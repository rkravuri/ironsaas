import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** A closed gate with a padlock: this is not for your account. */
export function Forbidden(props: IllustrationProps) {
  return (
    <Drawing name="forbidden" {...props}>
      <path d="M40 36 V106" />
      <path d="M120 36 V106" />
      <path d="M40 42 H120" />
      <path d="M40 78 H120" />
      <Fine d="M56 42 V106" />
      <Fine d="M72 42 V106" />
      <Fine d="M88 42 V106" />
      <Fine d="M104 42 V106" />
      <Tint d="M68 68 H92 V92 H68 Z" />
      <path d="M68 68 H92 V92 H68 Z" />
      <path d="M73 68 V60 a7 7 0 0 1 14 0 V68" />
      <circle cx={80} cy={80} r={2.5} />
      <Ground />
    </Drawing>
  );
}
