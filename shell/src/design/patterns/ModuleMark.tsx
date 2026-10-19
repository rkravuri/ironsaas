import type { AccentName } from "../tokens";
import { Mandala, type MandalaProps } from "./Mandala";
import { moduleMarkParameters } from "./module-mark";

export interface ModuleMarkProps extends Pick<MandalaProps, "size" | "animate" | "opacity" | "className" | "style"> {
  /** The module's name in its manifest: the seed, so a module's mark is the same on every page and every tenant. */
  name: string;
  accent: AccentName;
}

/** A module's mark: on its opener on the home page and beside the title of its first page. */
export function ModuleMark({ name, accent, ...rest }: ModuleMarkProps) {
  return <Mandala {...moduleMarkParameters(name)} accent={accent} {...rest} />;
}
