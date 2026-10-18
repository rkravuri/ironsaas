import { Drawing, Fine, Ground, Tint, type IllustrationProps } from "./Drawing";

/** An alarm clock on its side with its spring out: something stopped working, and not on purpose. */
export function Broken(props: IllustrationProps) {
  return (
    <Drawing name="broken" {...props}>
      <Tint cx={72} cy={72} r={28} />
      <circle cx={72} cy={72} r={28} />
      <Fine d="M72 48 V52" />
      <Fine d="M96 72 H92" />
      <Fine d="M72 96 V92" />
      <Fine d="M48 72 H52" />
      <path d="M72 72 L88 62" />
      <path d="M72 72 L66 58" />
      <circle cx={40} cy={58} r={5} />
      <circle cx={40} cy={86} r={5} />
      <Fine d="M45 60 L50 64" />
      <Fine d="M45 84 L50 80" />
      <path d="M94 50 L100 42 L106 50 L112 42 L118 50 L124 42" />
      <Fine d="M62 62 L70 72 L64 82" />
      <Ground />
    </Drawing>
  );
}
