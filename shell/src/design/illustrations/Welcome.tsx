import { Drawing, Ground, Tint, type IllustrationProps } from "./Drawing";

/** An open door with light inside, someone waving from it, and a plant by the step. */
export function Welcome(props: IllustrationProps) {
  return (
    <Drawing name="welcome" {...props}>
      <Tint d="M86 30 H114 V106 H86 Z" />
      <path d="M58 18 H114 V106" />
      <path d="M58 18 V106" />
      <path d="M58 18 L86 30 V106 L58 106" />
      <circle cx={80} cy={64} r={2} />
      <circle cx={100} cy={52} r={7} />
      <path d="M100 59 V84" />
      <path d="M100 84 L94 106" />
      <path d="M100 84 L106 106" />
      <path d="M100 66 L112 56 L116 46" />
      <path d="M100 66 L92 78" />
      <path d="M28 94 H46 L44 106 H30 Z" />
      <path d="M37 94 V74" />
      <path d="M37 84 q-8 -4 -10 -14" />
      <path d="M37 80 q8 -4 10 -14" />
      <Ground />
    </Drawing>
  );
}
