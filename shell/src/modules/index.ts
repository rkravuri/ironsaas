import type { ModuleManifest } from "./registry";
import { auditManifest } from "./kernel/audit/manifest";
import { complianceManifest } from "./kernel/compliance/manifest";
import { consoleManifest } from "./kernel/console/manifest";
import { identityManifest } from "./kernel/identity/manifest";

/**
 * Every module the shell knows, in one list, whatever its layer: modules live in kernel/, business/ or
 * suites/<suite>/ (ADR-0029, ADR-0043), and this file is the one place that imports every layer and suite. A suite's
 * manifest says its suite, so the home page groups it; entitlements decide what a tenant sees (ADR-0034).
 */
export const manifests: ModuleManifest[] = [identityManifest, consoleManifest, auditManifest, complianceManifest];
