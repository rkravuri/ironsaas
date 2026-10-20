import { describe, expect, it } from "vitest";
import {
  declaredRoutes,
  ManifestError,
  navigationFor,
  openersFor,
  quickActionsFor,
  routeAt,
  routesFor,
  validateManifests,
  type ModuleManifest,
} from "./registry";

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
      routes: [
        ...manifests[0]!.routes,
        { path: "/users/:id", permission: "identity.users.read", element: null, titleKey: "modules.identity.users" },
      ],
      opener: { accent: "identity", lineKey: "modules.identity.line", entries: 1 },
      quickActions: [
        { id: "invite", labelKey: "users.invite.open", permission: "identity.users.manage", path: "/users?invite=1" },
      ],
    },
    {
      name: "console",
      titleKey: "modules.console.title",
      routes: [
        { path: "/console/units", permission: "authz.units.read", element: null, titleKey: "modules.console.units" },
      ],
      navigation: [{ labelKey: "modules.console.units", path: "/console/units", permission: "authz.units.read" }],
      quickActions: [
        { id: "add-unit", labelKey: "units.quick.add", permission: "authz.units.manage", path: "/console/units?add=1" },
      ],
    },
  ];

  it("opens each module the principal may open, with as many first entries as its opener asks", () => {
    const openers = openersFor(modules, ["identity.users.read", "authz.units.read"], { strict: true });
    expect(
      openers.map((opening) => [opening.module, opening.opener?.accent, opening.entries.map((entry) => entry.path)]),
    ).toEqual([
      ["identity", "identity", ["/users"]],
      ["console", undefined, ["/console/units"]],
    ]);
    expect(openersFor(modules, [], { strict: true }).map((opening) => opening.module)).toEqual(["identity"]);
  });

  it("offers a quick action only where its permission is held somewhere; /me/access decides the rest", () => {
    expect(quickActionsFor(modules, ["identity.users.manage"]).map((action) => [action.module, action.id])).toEqual([
      ["identity", "invite"],
    ]);
    expect(quickActionsFor(modules, ["identity.users.read"])).toEqual([]);
  });

  it("finds the route a path belongs to, parameters included", () => {
    expect(routeAt(modules, "/users/42")?.path).toBe("/users/:id");
    expect(routeAt(modules, "/console/units")?.module).toBe("console");
    expect(routeAt(modules, "/nowhere")).toBeUndefined();
  });

  it("refuses a quick action without a permission", () => {
    const careless: ModuleManifest[] = [
      {
        name: "careless",
        titleKey: "x",
        routes: [],
        navigation: [],
        quickActions: [{ id: "oops", labelKey: "x", permission: "", path: "/" }],
      },
    ];
    expect(validateManifests(careless, { strict: false })).toEqual([
      "quick action careless:oops declares no permission",
    ]);
  });
});

describe("a licensed module in the manifests (ADR-0034)", () => {
  const licensed: ModuleManifest[] = [
    ...manifests,
    {
      name: "sourcing",
      titleKey: "modules.sourcing.title",
      licence: "sourcing",
      routes: [{ path: "/sourcing/events", permission: "sourcing.events.read", element: null, titleKey: "x" }],
      navigation: [
        { labelKey: "modules.sourcing.events", path: "/sourcing/events", permission: "sourcing.events.read" },
      ],
      opener: { accent: "primary", lineKey: "x" },
      quickActions: [
        { id: "new-event", labelKey: "x", permission: "sourcing.events.read", path: "/sourcing/events?new=1" },
      ],
    },
  ];
  const permissions = ["identity.users.read", "sourcing.events.read"];

  it("hides its navigation, routes, opener and quick actions while it is not entitled, whatever the permissions", () => {
    const none = () => false;
    expect(navigationFor(licensed, permissions, { strict: true }, none).map((g) => g.module)).toEqual(["identity"]);
    expect(routesFor(licensed, permissions, { strict: true }, none).map((r) => r.path)).toEqual(["/users", "/me"]);
    expect(openersFor(licensed, permissions, { strict: true }, none).map((o) => o.module)).toEqual(["identity"]);
    expect(quickActionsFor(licensed, permissions, none)).toEqual([]);
  });

  it("shows them once it is entitled, and a caller that does not say what is entitled hides them", () => {
    const sourcing = (licence: string) => licence === "sourcing";
    expect(navigationFor(licensed, permissions, { strict: true }, sourcing).map((g) => g.module)).toEqual([
      "identity",
      "sourcing",
    ]);
    expect(quickActionsFor(licensed, permissions, sourcing).map((a) => a.id)).toEqual(["new-event"]);
    expect(navigationFor(licensed, permissions, { strict: true }).map((g) => g.module)).toEqual(["identity"]);
  });

  it("carries the licence on its routes, so the route guard can check it on a link typed by hand", () => {
    expect(declaredRoutes(licensed, { strict: true }).find((r) => r.path === "/sourcing/events")?.licence).toBe(
      "sourcing",
    );
    expect(routeAt(licensed, "/sourcing/events")?.licence).toBe("sourcing");
    expect(routeAt(licensed, "/users")?.licence).toBeUndefined();
  });
});
