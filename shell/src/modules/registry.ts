import type { ComponentType, ReactNode } from "react";
import { matchPath } from "react-router";
import type { Shortcut } from "@/components/shortcuts";
import type { IllustrationProps } from "@/design/illustrations";
import type { AccentName } from "@/design/tokens";

export type { Shortcut } from "@/components/shortcuts";

/**
 * What a module tells the shell (S1 plan 3.10): its routes with the permission each needs, and its navigation
 * entries. The shell filters both by the permissions from /me and, for a licensed module, by its entitlement from
 * /me/context (ADR-0034). Flags join in A5.
 */
export interface ModuleRoute {
  /** Absolute path, e.g. "/users". */
  path: string;
  /**
   * The permission the route needs, or null for any signed-in principal. Leaving it out is a mistake: the shell
   * throws in development and hides the route in production.
   */
  permission?: string | null;
  element: ReactNode;
  /** i18n key of the page title. */
  titleKey: string;
  /** The page's own keys, for the help panel; the shell's keys are there for every page. */
  shortcuts?: Shortcut[];
}

export interface NavigationEntry {
  /** i18n key of the label. */
  labelKey: string;
  path: string;
  permission?: string | null;
  icon?: ReactNode;
}

/**
 * How a module opens on the home page (ADR-0033): in its accent, with its mark (the generator seeded by the module's
 * name), one line on what it is for, a spot drawing if it has one, and its first navigation entries as links.
 */
export interface ModuleOpener {
  accent: AccentName;
  /** i18n key of the one line. */
  lineKey: string;
  /** A spot drawing of the module's own, in the brief's hand (ADR-0026); none is fine. */
  drawing?: ComponentType<IllustrationProps>;
  /** How many of its visible navigation entries the opener lists; three unless it says otherwise. */
  entries?: number;
}

/** Something a person may want to start from anywhere: the home page and the search offer it (ADR-0033). */
export interface QuickAction {
  id: string;
  labelKey: string;
  /** The permission it needs, asked of /me/access at the principal's home unit; it cannot be left out. */
  permission: string;
  /** Where it happens: the page that owns the action opens it from this address. */
  path: string;
  icon?: ReactNode;
}

export interface ModuleManifest {
  name: string;
  /**
   * The licence code of a Business or suite module, as its IModule declares it (ADR-0034). A module that is not
   * entitled has no navigation, no routes, no opener and no quick actions. Kernel modules are the platform and have none.
   */
  licence?: string;
  /**
   * The short name of the suite a module under suites/<suite>/ belongs to, "procurement" (ADR-0043); the home page groups
   * its opener under that suite. A kernel or business module is the Foundation's and declares none. A test holds the
   * declaration to the module's folder.
   */
  suite?: string;
  /** i18n key of the module's title, shown as the navigation group. */
  titleKey: string;
  routes: ModuleRoute[];
  navigation: NavigationEntry[];
  opener?: ModuleOpener;
  quickActions?: QuickAction[];
}

export class ManifestError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ManifestError";
  }
}

export interface RegistryOptions {
  /** true in a development build: mistakes throw instead of hiding. */
  strict: boolean;
}

export type RegisteredRoute = ModuleRoute & { module: string; licence?: string };

/** Whether a licensed module is entitled for the signed-in person: the session's answer from /me/context. */
export type Entitled = (licence: string) => boolean;

/** Nothing licensed is entitled: what a caller that does not say gets, so a forgotten argument hides rather than shows. */
export const nothingEntitled: Entitled = () => false;

/** A manifest's module may be shown: a kernel module always, a licensed one when it is entitled. */
export const isEntitled = (manifest: ModuleManifest, entitled: Entitled): boolean =>
  manifest.licence === undefined || entitled(manifest.licence);

const withLicence = (route: ModuleRoute, manifest: ModuleManifest): RegisteredRoute => ({
  ...route,
  module: manifest.name,
  ...(manifest.licence !== undefined ? { licence: manifest.licence } : {}),
});

/** The routes a principal with these permissions may see; a route without a declared permission throws in strict mode. */
export function routesFor(
  manifests: ModuleManifest[],
  permissions: readonly string[],
  options: RegistryOptions,
  entitled: Entitled = nothingEntitled,
): RegisteredRoute[] {
  const granted = new Set(permissions);
  const routes: RegisteredRoute[] = [];
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (!declares(route, `route ${manifest.name}:${route.path}`, options) || !isEntitled(manifest, entitled)) {
        continue;
      }
      if (route.permission === null || granted.has(route.permission as string)) {
        routes.push(withLicence(route, manifest));
      }
    }
  }
  return routes;
}

/**
 * Every declared route, whatever the permissions: the router is built from this once, and the permission is checked
 * when the route renders. Filtering the router itself by permissions would make a deep link or a reload answer
 * "not found" before /me has been read.
 */
export function declaredRoutes(manifests: ModuleManifest[], options: RegistryOptions): RegisteredRoute[] {
  const routes: RegisteredRoute[] = [];
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (declares(route, `route ${manifest.name}:${route.path}`, options)) {
        routes.push(withLicence(route, manifest));
      }
    }
  }
  return routes;
}

export interface NavigationGroup {
  module: string;
  titleKey: string;
  entries: NavigationEntry[];
}

/** The navigation a principal with these permissions sees: entitled modules with at least one visible entry, in manifest order. */
export function navigationFor(
  manifests: ModuleManifest[],
  permissions: readonly string[],
  options: RegistryOptions,
  entitled: Entitled = nothingEntitled,
): NavigationGroup[] {
  const granted = new Set(permissions);
  const groups: NavigationGroup[] = [];
  for (const manifest of manifests) {
    if (!isEntitled(manifest, entitled)) {
      continue;
    }
    const entries = manifest.navigation.filter(
      (entry) =>
        declares(entry, `navigation ${manifest.name}:${entry.path}`, options) &&
        (entry.permission === null || granted.has(entry.permission as string)),
    );
    if (entries.length > 0) {
      groups.push({ module: manifest.name, titleKey: manifest.titleKey, entries });
    }
  }
  return groups;
}

/** The group of the kernel and business modules on the home page, before any suite's (ADR-0043). */
export const foundationSuite = "foundation";

/** A module's opener on the home page, for a principal who may open at least one of its entries. */
export interface ModuleOpening {
  module: string;
  /** The module's suite, or the Foundation's group for a kernel or business module. */
  suite: string;
  titleKey: string;
  opener: ModuleOpener | undefined;
  /** The first visible entries, as many as the opener asks for. */
  entries: NavigationEntry[];
}

/** The openers a principal with these permissions sees: one per module of the navigation, in its order. */
export function openersFor(
  manifests: ModuleManifest[],
  permissions: readonly string[],
  options: RegistryOptions,
  entitled: Entitled = nothingEntitled,
): ModuleOpening[] {
  const byName = new Map(manifests.map((manifest) => [manifest.name, manifest]));
  return navigationFor(manifests, permissions, options, entitled).map((group) => {
    const opener = byName.get(group.module)?.opener;
    return {
      module: group.module,
      suite: byName.get(group.module)?.suite ?? foundationSuite,
      titleKey: group.titleKey,
      opener,
      entries: group.entries.slice(0, opener?.entries ?? 3),
    };
  });
}

/** The openers of one suite, or of the Foundation. */
export interface SuiteOpenings {
  suite: string;
  openings: ModuleOpening[];
}

/** Whether the tenant's unit runs a suite (TF-244): the session's answer from /me/context. */
export type Installed = (suite: string) => boolean;

/** No suite is installed: what a caller that does not say gets, so a forgotten argument hides rather than shows. */
export const nothingInstalled: Installed = () => false;

/**
 * The openers grouped by suite (ADR-0043): the Foundation's first, then each suite in the order its first module is
 * listed, each keeping the order of the list. A group with no opener the principal may open is not there, and neither
 * is the group of a suite the tenant's unit does not run (TF-244, ADR-0059).
 */
export function openersBySuite(openings: ModuleOpening[], installed: Installed = nothingInstalled): SuiteOpenings[] {
  const groups = new Map<string, ModuleOpening[]>([[foundationSuite, []]]);
  for (const opening of openings) {
    groups.set(opening.suite, [...(groups.get(opening.suite) ?? []), opening]);
  }
  return [...groups]
    .filter(([suite, members]) => members.length > 0 && (suite === foundationSuite || installed(suite)))
    .map(([suite, members]) => ({ suite, openings: members }));
}

/** The quick actions of entitled modules whose permission the principal holds somewhere; /me/access then says whether at its home unit. */
export function quickActionsFor(
  manifests: ModuleManifest[],
  permissions: readonly string[],
  entitled: Entitled = nothingEntitled,
): (QuickAction & { module: string })[] {
  const granted = new Set(permissions);
  return manifests
    .filter((manifest) => isEntitled(manifest, entitled))
    .flatMap((manifest) =>
      (manifest.quickActions ?? [])
        .filter((action) => granted.has(action.permission))
        .map((action) => ({ ...action, module: manifest.name })),
    );
}

/** The declared route a path belongs to, for the help panel's keys and the name of a page opened recently. */
export function routeAt(manifests: ModuleManifest[], pathname: string): RegisteredRoute | undefined {
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (matchPath({ path: route.path, end: true }, pathname)) {
        return withLicence(route, manifest);
      }
    }
  }
  return undefined;
}

/** Every route and entry declares its permission (null counts); a path declared twice is a mistake too. */
export function validateManifests(manifests: ModuleManifest[], options: RegistryOptions): string[] {
  const problems: string[] = [];
  const paths = new Set<string>();
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (route.permission === undefined) {
        problems.push(
          `route ${manifest.name}:${route.path} declares no permission (use null for any signed-in principal)`,
        );
      }
      if (paths.has(route.path)) {
        problems.push(`route ${route.path} is declared twice`);
      }
      paths.add(route.path);
    }
    for (const entry of manifest.navigation) {
      if (entry.permission === undefined) {
        problems.push(`navigation ${manifest.name}:${entry.path} declares no permission`);
      }
    }
    for (const action of manifest.quickActions ?? []) {
      if (!action.permission) {
        problems.push(`quick action ${manifest.name}:${action.id} declares no permission`);
      }
    }
  }
  if (options.strict && problems.length > 0) {
    throw new ManifestError(problems.join("; "));
  }
  return problems;
}

function declares(item: { permission?: string | null }, what: string, options: RegistryOptions): boolean {
  if (item.permission !== undefined) {
    return true;
  }
  if (options.strict) {
    throw new ManifestError(`${what} declares no permission (use null for any signed-in principal)`);
  }
  return false;
}
