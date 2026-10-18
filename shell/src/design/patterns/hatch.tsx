/** A hatch for shading (brief section 3): thin diagonal lines in the ink, used as a fill through url(#id). */
export function Hatch({ id, spacing = 4 }: { id: string; spacing?: number }) {
  return (
    <pattern id={id} patternUnits="userSpaceOnUse" width={spacing} height={spacing} patternTransform="rotate(45)">
      <line x1={0} y1={0} x2={0} y2={spacing} stroke="var(--iron-ink)" strokeWidth={1} strokeOpacity={0.45} />
    </pattern>
  );
}
