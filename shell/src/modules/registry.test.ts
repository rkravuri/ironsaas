import { describe, expect, it } from "vitest";
import { declaredRoutes, ManifestError, navigationFor, openersFor, quickActionsFor, routeAt, routesFor, validateManifests, type ModuleManifest } from "./registry";

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

describe("what the home page and the search read from the manifests (ADR-0033)", () => {
  const modules: ModuleManifest[] = [
    {
      ...manifests[0]!,
      routes: [...manifests[0]!.routes, { path: "/users/:id", permission: "identity.users.read", element: null, titleKey: "modules.identity.users" }],
      opener: { accent: "identity", lineKey: "modules.identity.line", entries: 1 },
      quickActions: [{ id: "invite", labelKey: "users.invite.open", permission: "identity.users.manage", path: "/users?invite=1" }],
    },
    {
      name: "console",
      titleKey: "modules.console.title",
      routes: [{ path: "/console/units", permission: "authz.units.read", element: null, titleKey: "modules.console.units" }],
      navigation: [{ labelKey: "modules.console.units", path: "/console/units", permission: "authz.units.read" }],
      quickActions: [{ id: "add-unit", labelKey: "units.quick.add", permission: "authz.units.manage", path: "/console/units?add=1" }],
    },
  ];

  it("opens each module the principal may open, with as many first entries as its opener asks", () => {
    const openers = openersFor(modules, ["identity.users.read", "authz.units.read"], { strict: true });
    expect(openers.map((opening) => [opening.module, opening.opener?.accent, opening.entries.map((entry) => entry.path)])).toEqual([
      ["identity", "identity", ["/users"]],
      ["console", undefined, ["/console/units"]],
    ]);
    expect(openersFor(modules, [], { strict: true }).map((opening) => opening.module)).toEqual(["identity"]);
  });

  it("offers a quick action only where its permission is held somewhere; /me/access decides the rest", () => {
    expect(quickActionsFor(modules, ["identity.users.manage"]).map((action) => [action.module, action.id])).toEqual([["identity", "invite"]]);
    expect(quickActionsFor(modules, ["identity.users.read"])).toEqual([]);
  });

  it("finds the route a path belongs to, parameters included", () => {
    expect(routeAt(modules, "/users/42")?.path).toBe("/users/:id");
    expect(routeAt(modules, "/console/units")?.module).toBe("console");
    expect(routeAt(modules, "/nowhere")).toBeUndefined();
  });

  it("refuses a quick action without a permission", () => {
    const careless: ModuleManifest[] = [{ name: "careless", titleKey: "x", routes: [], navigation: [], quickActions: [{ id: "oops", labelKey: "x", permission: "", path: "/" }] }];
    expect(validateManifests(careless, { strict: false })).toEqual(["quick action careless:oops declares no permission"]);
  });
});
