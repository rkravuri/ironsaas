import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** Someone holding a stamped document up high, with a little confetti. */
export function Success(props: IllustrationProps) {
  return (
    <Drawing name="success" {...props}>
      <path d="M48 12 H96 V40 H48 Z" />
      <Fine d="M56 22 H78" />
      <Fine d="M56 30 H72" />
      <Tint cx={86} cy={30} r={7} />
      <circle cx={86} cy={30} r={7} />
      <path d="M82 30 L85 33 L90 27" />
      <circle cx={72} cy={52} r={8} />
      <path d="M72 60 V84" />
      <path d="M72 84 L62 106" />
      <path d="M72 84 L82 106" />
      <path d="M72 66 L56 40" />
      <path d="M72 66 L88 40" />
      <circle cx={30} cy={36} r={1.5} />
      <circle cx={124} cy={28} r={1.5} />
      <circle cx={132} cy={64} r={1.5} />
      <circle cx={24} cy={70} r={1.5} />
      <Ground />
    </Drawing>
  );
}
