import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router";
import { fill, json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { SsoProviderPage } from "./SsoProviderPage";
import { SsoProvidersPage } from "./SsoProvidersPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const manage = ["identity.sso.manage"];
const providerId = "22222222-2222-7222-8222-222222222222";
const page = (data: unknown[]) => ({
  data,
  meta: {
    page_number: 1,
    page_size: 50,
    total_count: data.length,
    total_pages: 1,
    has_previous: false,
    has_next: false,
    page_size_capped: false,
    max_page_size: 200,
  },
  links: { self: "/", first: "/", previous: null, next: null, last: "/" },
});
const role = (code: string, name: string) => ({
  id: `id-${code}`,
  code,
  name,
  description: "",
  is_system: false,
  principal_type: "user",
  permissions: ["identity.users.read"],
  created_at: "2026-10-01T00:00:00Z",
  updated_at: "2026-10-01T00:00:00Z",
  version: 1,
});
const root = {
  id: "root",
  name: "Acme",
  code: "acme",
  kind: "organisation",
  parent_id: null,
  depth: 0,
  status: "active",
  created_at: "2026-10-01T00:00:00Z",
  updated_at: "2026-10-01T00:00:00Z",
  version: 1,
};
const saved = {
  id: providerId,
  name: "Contoso Entra ID",
  preset: "entra_id",
  discovery_url: "https://login.microsoftonline.com/0f1e2d3c/v2.0/.well-known/openid-configuration",
  client_id: "iron-client",
  allowed_domains: ["contoso.test"],
  email_claim: "email",
  name_claim: "name",
  groups_claim: "groups",
  just_in_time: true,
  default_role_code: null,
  default_home_unit_id: null,
  sso_only: false,
  trust_provider_mfa: true,
  role_mappings: [],
  created_at: "2026-10-22T09:00:00Z",
  updated_at: "2026-10-22T09:00:00Z",
  version: 1,
};

function stubs(extra: Parameters<typeof stubFetch>[0] = {}) {
  return stubFetch({
    "GET /api/v1/me": () => json(me(manage)),
    "GET /api/v1/roles?page_size=200&sort=name": () => json(page([role("readers", "Readers")])),
    "GET /api/v1/organisation-units": () => json({ data: [root] }),
    ...extra,
  });
}

function open(route: string) {
  renderWithProviders(
    <Routes>
      <Route path="/console/sso" element={<SsoProvidersPage />} />
      <Route path="/console/sso/new" element={<SsoProviderPage />} />
      <Route path="/console/sso/:id" element={<SsoProviderPage />} />
    </Routes>,
    { route },
  );
}

async function choose(user: ReturnType<typeof userEvent.setup>, label: string, option: string) {
  // The label names the input and its list of options; the input opens the list it controls.
  const input = (await screen.findAllByLabelText(label)).find((element) => element.tagName === "INPUT")!;
  await user.click(input);
  const list = document.getElementById(input.getAttribute("aria-controls") ?? "");
  const options = list
    ? [await within(list).findByRole("option", { name: option, hidden: true })]
    : await screen.findAllByRole("option", { name: option, hidden: true });
  fireEvent.click(options[options.length - 1]!);
}

describe("the single sign-on pages", () => {
  it("lists the company logins with what each does, and offers to connect one [ADR-0048]", async () => {
    const { calls } = stubs({
      "GET /api/v1/sso/providers?page_number=1&page_size=50&sort=name": () => json(page([saved])),
    });
    open("/console/sso");

    expect(await screen.findByRole("heading", { name: "Single sign-on" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Connect a provider" })).toHaveAttribute("href", "/console/sso/new");
    await waitFor(() => expect(calls.some((c) => c.key.startsWith("GET /api/v1/sso/providers"))).toBe(true));
  });

  it("fills the discovery address from a preset, tests the connection in words, and connects the provider with its secret [TF-014 AC6, ADR-0048]", async () => {
    const { calls } = stubs({
      "POST /api/v1/sso/test": () =>
        json({
          passed: true,
          items: [
            {
              check: "discovery",
              status: "passed",
              message: "Read the discovery document of https://login.example.test.",
            },
            { check: "keys", status: "passed", message: "Read 2 signing keys." },
            { check: "end_session", status: "warning", message: "The provider publishes no end-session endpoint." },
          ],
        }),
      "POST /api/v1/sso/providers": () => json(saved, 201),
      [`GET /api/v1/sso/providers/${providerId}`]: () => json(saved),
    });
    open("/console/sso/new");
    const user = userEvent.setup();

    await choose(user, "Provider", "Microsoft Entra ID");
    await fill(user, await screen.findByLabelText(/Directory \(tenant\) ID/), "0f1e2d3c");
    expect(screen.getByLabelText(/^Discovery address/)).toHaveValue(saved.discovery_url);
    await user.click(screen.getByRole("button", { name: "Test connection" }));
    const report = await screen.findByText(/The connection works\./);
    const list = report.closest("[data-sso-test-report]") as HTMLElement;
    expect(within(list).getByText(/Passed: Signing keys\./)).toBeInTheDocument();
    expect(within(list).getByText(/Warning: Sign-out at the provider\./)).toBeInTheDocument();

    await fill(user, screen.getByLabelText("Name"), "Contoso Entra ID");
    await fill(user, screen.getByLabelText(/^Client ID/), "iron-client");
    await fill(user, screen.getByLabelText(/^Client secret/), "the client secret");
    const domains = screen.getAllByLabelText(/^Allowed email domains/).find((element) => element.tagName === "INPUT")!;
    await user.type(domains, "contoso.test{enter}");
    await user.click(screen.getByRole("button", { name: "Connect the provider" }));

    await waitFor(() => expect(calls.some((c) => c.key === "POST /api/v1/sso/providers")).toBe(true));
    const body = JSON.parse(calls.find((c) => c.key === "POST /api/v1/sso/providers")?.init?.body as string) as Record<
      string,
      unknown
    >;
    expect(body).toMatchObject({
      name: "Contoso Entra ID",
      preset: "entra_id",
      discovery_url: saved.discovery_url,
      client_id: "iron-client",
      client_secret: "the client secret",
      allowed_domains: ["contoso.test"],
      trust_provider_mfa: true,
      role_mappings: [],
    });
  });

  it("keeps the stored secret when the field is left empty, and shows a mapping the author may not grant as refused [TF-014 AC4, ADR-0031, ADR-0048]", async () => {
    let put = 0;
    const { calls } = stubs({
      [`GET /api/v1/sso/providers/${providerId}`]: () => json(saved),
      [`PUT /api/v1/sso/providers/${providerId}`]: () =>
        put++ === 0
          ? json(saved)
          : problem(
              403,
              {
                title: "Forbidden",
                detail: "A provider may give only roles you may grant yourself.",
                error_code: "identity.sso.escalation",
                errors: [{ field: "role_mappings", code: "escalation", message: "You may not grant 'readers' there." }],
              },
              "corr-escalation",
            ),
    });
    open(`/console/sso/${providerId}`);
    const user = userEvent.setup();

    expect(await screen.findByLabelText(/^Client secret/)).toHaveValue("");
    await user.click(screen.getByRole("button", { name: "Save changes" }));
    await screen.findByText("Saved.");
    const first = JSON.parse(
      calls.find((c) => c.key === `PUT /api/v1/sso/providers/${providerId}`)?.init?.body as string,
    ) as Record<string, unknown>;
    expect(first.client_secret).toBeNull();

    await user.click(screen.getByRole("button", { name: "Add a mapping" }));
    await fill(user, screen.getByLabelText("Group"), "buyers");
    await choose(user, "Role", "Readers");
    await user.click(screen.getByRole("button", { name: "Save changes" }));
    expect(await screen.findByText("You may not grant 'readers' there.")).toBeInTheDocument();
  });
});
