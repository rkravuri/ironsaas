import type { ModuleManifest } from "./registry";
import { consoleManifest } from "./kernel/console/manifest";
import { identityManifest } from "./kernel/identity/manifest";

/**
 * Every module the shell knows, in one list, whatever its layer: modules live in kernel/, business/ or products/
 * (ADR-0029), and this file is the one place that imports all three. S2 makes the list follow entitlements.
 */
export const manifests: ModuleManifest[] = [identityManifest, consoleManifest];
