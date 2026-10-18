import type { ModuleManifest } from "./registry";
import { identityManifest } from "./identity/manifest";

/** Every module the shell knows. Products add theirs here; S2 makes the list follow entitlements. */
export const manifests: ModuleManifest[] = [identityManifest];
