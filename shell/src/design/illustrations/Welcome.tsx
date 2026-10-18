import { Drawing, Figure, Ground, Tint, type IllustrationProps } from "./Drawing";

/** An open door with light inside, someone waving from it, and a plant by the step. */
export function Welcome(props: IllustrationProps) {
  return (
    <Drawing name="welcome" {...props}>
      <Tint d="M86 30 H114 V106 H86 Z" />
      <path d="M58 18 H114 V106" />
      <path d="M58 18 V106" />
      <path d="M58 18 L86 30 V106 L58 106" />
      <circle cx={80} cy={64} r={2} />
      <Figure x={100} y={48} pose="waving" />
      <path d="M28 94 H46 L44 106 H30 Z" />
      <path d="M37 94 V74" />
      <path d="M37 84 q-8 -4 -10 -14" />
      <path d="M37 80 q8 -4 10 -14" />
      <Ground />
    </Drawing>
  );
}
