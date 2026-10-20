import { useMemo, type CSSProperties } from "react";
import { usePrefersReducedMotion } from "../motion";
import type { AccentName } from "../tokens";
import { mandala, type MandalaParameters } from "./mandala";
import "./patterns.css";

export interface MandalaProps extends MandalaParameters {
  /** Width and height in pixels. */
  size: number;
  /** Opacity of the accent rings: 1 for a mark, lower for a texture. */
  accentOpacity?: number;
  /** The accent of the accent ring: the platform's by default, a module's for its own opener (brief section 5). */
  accent?: AccentName;
  /** Opacity of the whole drawing; a texture behind a moment sits at 0.06 to 0.08 (brief section 4). */
  opacity?: number;
  /** Draws itself in on mount; never when the viewer asked for less motion. */
  animate?: boolean;
  className?: string;
  style?: CSSProperties;
}

/** A mandala as decoration: hidden from assistive technology, the ink on most rings and the accent on one. */
export function Mandala({
  symmetry,
  rings,
  stroke,
  density,
  seed,
  size,
  accentOpacity = 1,
  accent = "primary",
  opacity = 1,
  animate = false,
  className,
  style,
}: MandalaProps) {
  // The generator caches by input too; the memo keeps the same object across renders of this element.
  const drawing = useMemo(
    () => mandala({ symmetry, rings, stroke, density, seed }),
    [symmetry, rings, stroke, density, seed],
  );
  const reduced = usePrefersReducedMotion();
  const drawIn = animate && !reduced;

  return (
    <svg
      aria-hidden="true"
      focusable="false"
      data-mandala={String(seed)}
      viewBox={drawing.viewBox}
      width={size}
      height={size}
      fill="none"
      stroke="var(--iron-ink)"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      style={{ opacity, ...style }}
    >
      {drawing.rings.map((ring, index) => (
        <path
          key={ring.radius}
          d={ring.path}
          pathLength={1}
          strokeWidth={ring.strokeWidth}
          stroke={ring.accent ? `var(--iron-accent-${accent})` : undefined}
          strokeOpacity={ring.accent ? accentOpacity : undefined}
          className={drawIn ? "iron-draw-in" : undefined}
          style={drawIn ? { animationDelay: `${index * 120}ms` } : undefined}
        />
      ))}
    </svg>
  );
}
