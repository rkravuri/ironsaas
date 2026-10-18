import { Drawing, Figure, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** Someone holding a stamped document up high, with a little confetti. */
export function Success(props: IllustrationProps) {
  return (
    <Drawing name="success" {...props}>
      <path d="M48 10 H96 V38 H48 Z" />
      <Fine d="M56 20 H78" />
      <Fine d="M56 28 H72" />
      <Tint cx={86} cy={28} r={7} />
      <circle cx={86} cy={28} r={7} />
      <path d="M82 28 L85 31 L90 25" />
      <Figure x={72} y={48} pose="holding" />
      <circle cx={30} cy={36} r={1.5} />
      <circle cx={124} cy={28} r={1.5} />
      <circle cx={132} cy={64} r={1.5} />
      <circle cx={24} cy={70} r={1.5} />
      <Ground />
    </Drawing>
  );
}
