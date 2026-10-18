import { describe, expect, it } from "vitest";
import { declaredRoutes, ManifestError, navigationFor, routesFor, validateManifests, type ModuleManifest } from "./registry";

const manifests: ModuleManifest[] = [
  {
    name: "identity",
    titleKey: "modules.identity.title",
    routes: [
      { path: "/users", permission: "identity.users.read", element: null, titleKey: "modules.identity.users" },
      { path: "/me", permission: null, element: null, titleKey: "modules.identity.profile" },
    ],
    navigation: [
      { labelKey: "modules.identity.users", path: "/users", permission: "identity.users.read" },
      { labelKey: "modules.identity.profile", path: "/me", permission: null },
    ],
  },
];

describe("the module registry", () => {
  it("shows routes and navigation by the permissions from /me", () => {
    const routes = routesFor(manifests, ["identity.users.read"], { strict: true });
    expect(routes.map((r) => r.path)).toEqual(["/users", "/me"]);

    const fewer = routesFor(manifests, [], { strict: true });
    expect(fewer.map((r) => r.path)).toEqual(["/me"]);

    const navigation = navigationFor(manifests, ["identity.users.read"], { strict: true });
    expect(navigation).toHaveLength(1);
    expect(navigation[0]?.entries.map((e) => e.path)).toEqual(["/users", "/me"]);
  });

  it("declares every route for the router whatever the permissions, since the check happens at render time", () => {
    expect(declaredRoutes(manifests, { strict: true }).map((r) => r.path)).toEqual(["/users", "/me"]);
  });

  it("throws in development for a route without a declared permission, and hides it in production", () => {
    const careless: ModuleManifest[] = [
      { name: "careless", titleKey: "x", routes: [{ path: "/oops", element: null, titleKey: "x" }], navigation: [] },
    ];
    expect(() => validateManifests(careless, { strict: true })).toThrow(ManifestError);
    expect(() => routesFor(careless, ["anything"], { strict: true })).toThrow(/declares no permission/);

    expect(validateManifests(careless, { strict: false })).toHaveLength(1);
    expect(routesFor(careless, ["anything"], { strict: false })).toEqual([]);
  });

  it("reports a path declared twice", () => {
    const twice: ModuleManifest[] = [
      {
        name: "twice",
        titleKey: "x",
        routes: [
          { path: "/a", permission: null, element: null, titleKey: "x" },
          { path: "/a", permission: null, element: null, titleKey: "x" },
        ],
        navigation: [],
      },
    ];
    expect(validateManifests(twice, { strict: false })).toEqual(["route /a is declared twice"]);
  });
});
