import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** A small building with its flag up and a tree by the door: the place to start the day. */
export function Home(props: IllustrationProps) {
  return (
    <Drawing name="home" {...props}>
      <Tint d="M34 56 L70 30 L106 56 Z" />
      <path d="M34 56 L70 30 L106 56" />
      <path d="M42 56 V106 H98 V56" />
      <path d="M62 106 V82 H78 V106" />
      <Fine d="M48 66 H58 V76 H48 Z" />
      <Fine d="M82 66 H92 V76 H82 Z" />
      <path d="M116 106 V34" />
      <path d="M116 34 H138 L132 42 L138 50 H116 Z" />
      <circle cx={24} cy={88} r={9} />
      <path d="M24 97 V106" />
      <Ground />
    </Drawing>
  );
}
