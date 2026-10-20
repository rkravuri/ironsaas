import { useMemo } from "react";
import { Mandala } from "./Mandala";
import { unitMarkParameters } from "./unit-mark";

export interface UnitMarkProps {
  /** The unit's code, which seeds the drawing. */
  code: string;
  /** Width and height in pixels: 18 in the header's control and its menu. */
  size: number;
}

/** An organisation unit's mark (ADR-0038): the mandala seeded from its code, decoration beside the unit's name. */
export function UnitMark({ code, size }: UnitMarkProps) {
  const parameters = useMemo(() => unitMarkParameters(code), [code]);
  return <Mandala {...parameters} size={size} />;
}
