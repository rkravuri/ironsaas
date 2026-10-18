import { useMemo, type CSSProperties } from "react";
import { Mandala } from "./Mandala";
import { tenantMarkParameters } from "./tenant-mark";

export interface TenantMarkProps {
  hostname: string;
  /** Width and height in pixels: 24 in the header, 32 beside the login title, large behind the login form. */
  size: number;
  opacity?: number;
  accentOpacity?: number;
  animate?: boolean;
  className?: string;
  style?: CSSProperties;
}

/** The tenant's mark: the mandala seeded from its hostname. TF-173 adds the tenant's colours later. */
export function TenantMark({ hostname, size, ...rest }: TenantMarkProps) {
  const parameters = useMemo(() => tenantMarkParameters(hostname), [hostname]);
  return <Mandala {...parameters} size={size} {...rest} />;
}
