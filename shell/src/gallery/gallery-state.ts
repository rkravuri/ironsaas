import type { Density } from "@/design/density";
import type { Scheme } from "@/design/tokens";

export interface GalleryState {
  scheme: Scheme;
  density: Density;
}

/** The scheme and density come from the URL, so a scan or a link lands on a known state. */
export function galleryStateFrom(params: URLSearchParams): GalleryState {
  return {
    scheme: params.get("scheme") === "dark" ? "dark" : "light",
    density: params.get("density") === "compact" ? "compact" : "comfortable",
  };
}
