import type { CSSProperties, ReactNode, SVGProps } from "react";
import { useTranslation } from "react-i18next";
import { drawingSizes, figure, hand, type DrawingSize, type FigurePose } from "./hand";
import type { IllustrationName } from "./names";

export interface IllustrationProps {
  /** Hidden from assistive technology when the text beside it already says what it shows. */
  decorative?: boolean;
  size?: DrawingSize;
  /** The ink and the accent default to the tokens' variables; a moment may pass another accent. */
  ink?: string;
  accent?: string;
  className?: string;
}

interface DrawingProps extends IllustrationProps {
  name: IllustrationName;
  children: ReactNode;
}

/**
 * The frame every illustration is drawn in: the hand's stroke, caps and joins, the viewBox, the named size, and the
 * accessible name from the catalogue or `aria-hidden` when the drawing is decoration beside its own words.
 */
export function Drawing({ name, decorative = false, size = "md", ink = hand.ink, accent = hand.accent, className, children }: DrawingProps) {
  const { t } = useTranslation();
  const width = drawingSizes[size];
  const height = Math.round((width * hand.height) / hand.width);
  const accessibility = decorative ? { "aria-hidden": true as const } : { role: "img" as const, "aria-label": t(`illustrations.${name}`) };
  return (
    <svg
      {...accessibility}
      focusable="false"
      data-drawing={name}
      viewBox={hand.viewBox}
      width={width}
      height={height}
      fill="none"
      stroke={ink}
      strokeWidth={hand.stroke}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      style={{ "--drawing-accent": accent } as CSSProperties}
    >
      {children}
    </svg>
  );
}

/** A flat fill in the accent at low opacity: the brief allows one or two per drawing. A circle when given a radius. */
export function Tint({ d, cx, cy, r }: { d?: string; cx?: number; cy?: number; r?: number }) {
  const tint = { fill: "var(--drawing-accent)", fillOpacity: hand.fillOpacity, stroke: "none" };
  return d !== undefined ? <path d={d} {...tint} /> : <circle cx={cx} cy={cy} r={r} {...tint} />;
}

/** The thinner stroke, for detail only. */
export function Fine(props: SVGProps<SVGPathElement>) {
  return <path strokeWidth={hand.detail} {...props} />;
}

/** The ground every drawing stands on: one thin line, so the set reads as one world. */
export function Ground() {
  return <Fine d="M28 108 H132" strokeOpacity={0.5} />;
}

/** A person, always through the hand's one figure: the posture says what the drawing means. */
export function Figure({ x, y, pose }: { x: number; y: number; pose: FigurePose }) {
  const parts = figure(x, y, pose);
  return (
    <g data-figure={pose}>
      <circle cx={parts.head.cx} cy={parts.head.cy} r={parts.head.r} />
      <path d={parts.cap} />
      <path d={parts.neck} />
      <path d={parts.torso} fill="var(--drawing-accent)" fillOpacity={hand.fillOpacity} />
      {parts.arms.map((d) => (
        <path key={d} d={d} />
      ))}
      {parts.legs.map((d) => (
        <path key={d} d={d} />
      ))}
      {parts.feet.map((d) => (
        <path key={d} d={d} />
      ))}
    </g>
  );
}
