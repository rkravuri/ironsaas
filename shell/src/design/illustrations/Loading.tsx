import { usePrefersReducedMotion } from "../motion";
import "../patterns/patterns.css";
import { Drawing, Fine, Ground, type IllustrationProps } from "./Drawing";

/** Three documents drawing themselves in, one after the other; a drawn loader, never a spinner (brief section 7). */
export function Loading(props: IllustrationProps) {
  const reduced = usePrefersReducedMotion();
  const sheet = (delay: number) =>
    reduced ? {} : { className: "iron-draw-loop", style: { animationDelay: `${delay}ms` } };
  return (
    <Drawing name="loading" {...props}>
      <path d="M40 34 H92 V98 H40 Z" pathLength={1} {...sheet(0)} />
      <Fine d="M50 50 H82" pathLength={1} {...sheet(100)} />
      <Fine d="M50 62 H76" pathLength={1} {...sheet(200)} />
      <Fine d="M50 74 H82" pathLength={1} {...sheet(300)} />
      <path d="M52 24 H104 V88" pathLength={1} {...sheet(400)} />
      <path d="M64 14 H116 V78" pathLength={1} {...sheet(800)} />
      <Ground />
    </Drawing>
  );
}
