import type { ReactNode } from "react";

/**
 * What a module tells the shell (S1 plan 3.10): its routes with the permission each needs, and its navigation
 * entries. The shell filters both by the permissions from /me. Entitlements and flags join in S2.
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
}

export interface NavigationEntry {
  /** i18n key of the label. */
  labelKey: string;
  path: string;
  permission?: string | null;
  icon?: ReactNode;
}

export interface ModuleManifest {
  name: string;
  /** i18n key of the module's title, shown as the navigation group. */
  titleKey: string;
  routes: ModuleRoute[];
  navigation: NavigationEntry[];
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

export type RegisteredRoute = ModuleRoute & { module: string };

/** The routes a principal with these permissions may see; a route without a declared permission throws in strict mode. */
export function routesFor(manifests: ModuleManifest[], permissions: readonly string[], options: RegistryOptions): RegisteredRoute[] {
  const granted = new Set(permissions);
  const routes: RegisteredRoute[] = [];
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (!declares(route, `route ${manifest.name}:${route.path}`, options)) {
        continue;
      }
      if (route.permission === null || granted.has(route.permission as string)) {
        routes.push({ ...route, module: manifest.name });
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
        routes.push({ ...route, module: manifest.name });
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

/** The navigation a principal with these permissions sees: modules with at least one visible entry, in manifest order. */
export function navigationFor(manifests: ModuleManifest[], permissions: readonly string[], options: RegistryOptions): NavigationGroup[] {
  const granted = new Set(permissions);
  const groups: NavigationGroup[] = [];
  for (const manifest of manifests) {
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

/** Every route and entry declares its permission (null counts); a path declared twice is a mistake too. */
export function validateManifests(manifests: ModuleManifest[], options: RegistryOptions): string[] {
  const problems: string[] = [];
  const paths = new Set<string>();
  for (const manifest of manifests) {
    for (const route of manifest.routes) {
      if (route.permission === undefined) {
        problems.push(`route ${manifest.name}:${route.path} declares no permission (use null for any signed-in principal)`);
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
