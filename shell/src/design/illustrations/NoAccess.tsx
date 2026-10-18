import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** A closed door with a keyhole, and a key that is not yours, on the floor beside it. */
export function NoAccess(props: IllustrationProps) {
  return (
    <Drawing name="noAccess" {...props}>
      <path d="M50 20 H106 V104 H50 Z" />
      <Fine d="M58 28 H98 V58 H58 Z" />
      <Fine d="M58 66 H98 V96 H58 Z" />
      <circle cx={94} cy={62} r={3.5} />
      <path d="M94 65.5 L92 74 H96 Z" />
      <Tint cx={118} cy={88} r={8} />
      <circle cx={118} cy={88} r={8} />
      <path d="M126 88 H148" />
      <Fine d="M142 88 V95" />
      <Fine d="M148 88 V93" />
      <Ground />
    </Drawing>
  );
}
