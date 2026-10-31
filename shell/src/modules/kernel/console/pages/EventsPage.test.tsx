import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fill, json, me, renderWithProviders, stubFetch } from "@/test/render";
import { eventFilterQuery } from "./event-filters";
import { EventsPage } from "./EventsPage";

afterEach(() => {
  vi.unstubAllGlobals();
});

const list = (data: unknown[]) => ({
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
  links: { self: "x", first: "x", previous: null, next: null, last: "x" },
});

const deactivated = {
  id: "0198f0c2-0000-7000-8000-0000000000e1",
  position: 41,
  type: "identity.person.deactivated",
  version: 1,
  record_type: "principal",
  record_id: "0198f0c2-0000-7000-8000-000000000001",
  actor_id: "0198f0c2-0000-7000-8000-000000000002",
  on_behalf_of_id: null,
  occurred_at: "2026-10-31T09:00:00Z",
  correlation_id: "0HN7Q1",
  data: { person_id: "0198f0c2-0000-7000-8000-000000000001", sessions_ended: 2 },
  status: "dead",
  deliveries: [
    {
      subscriber: "authz.end_delegations_of_deactivated",
      status: "dead",
      attempts: 6,
      next_attempt_at: "2026-10-31T09:20:00Z",
      last_error: "authz.delegation.locked: The delegation is being changed.",
      handled_at: null,
      dead_at: "2026-10-31T09:20:00Z",
      replays: 0,
    },
  ],
};

const types = [
  {
    name: "identity.person.deactivated",
    version: 1,
    module: "identity",
    record_type: "principal",
    description: "A person was deactivated.",
    owner: "Identity module",
    fields: [],
    deprecated_on: null,
    replaced_by: null,
    subscribers: [
      { name: "authz.end_delegations_of_deactivated", module: "authz", description: "Ends their delegations." },
    ],
  },
];

const handlers = (permissions: string[], extra: Record<string, (init: RequestInit | undefined) => Response> = {}) => ({
  "GET /api/v1/me": () => json(me(permissions)),
  "GET /api/v1/events/types": () => json(types),
  "GET /api/v1/events/retention": () => json({ days: 90, archived_through: null, archives: 0 }),
  "GET /api/v1/events?page_number=1&page_size=50&sort=-position": () => json(list([deactivated])),
  [`GET /api/v1/events/${deactivated.id}`]: () => json(deactivated),
  ...extra,
});

describe("the console's events page", () => {
  it("opens the event a link names with its data and where it stands with each subscriber, in words, and offers no replay to a reader [TF-112 AC1, ADR-0060, ADR-0024]", async () => {
    stubFetch(handlers(["events.events.read"]));
    renderWithProviders(<EventsPage />, { route: `/console/events?event=${deactivated.id}` });

    expect(await screen.findByRole("heading", { level: 2, name: "Domain events" })).toBeInTheDocument();
    const detail = await screen.findByRole("region", { name: "identity.person.deactivated (version 1)" });
    expect(detail).toHaveTextContent("principal 0198f0c2-0000-7000-8000-000000000001");
    expect(detail).toHaveTextContent('"sessions_ended": 2');
    expect(within(detail).getAllByText("Dead letter").length).toBeGreaterThan(0);
    expect(detail).toHaveTextContent("authz.delegation.locked: The delegation is being changed.");
    expect(screen.getByText(/stay searchable for 90 days/)).toBeInTheDocument();
    expect(within(detail).queryByRole("button", { name: /Replay to/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Replay a range" })).not.toBeInTheDocument();
  });

  it("replays a dead letter to its subscriber only with a reason, and says what the replay did [TF-112 AC1, ADR-0060]", async () => {
    const { calls } = stubFetch(
      handlers(["events.events.read", "events.events.replay"], {
        "POST /api/v1/events/replay": () =>
          json({
            subscriber: "authz.end_delegations_of_deactivated",
            events: 1,
            requeued: 1,
            already_handled: 0,
            added: 0,
          }),
      }),
    );
    renderWithProviders(<EventsPage />, { route: `/console/events?event=${deactivated.id}` });
    const user = userEvent.setup();

    const detail = await screen.findByRole("region", { name: "identity.person.deactivated (version 1)" });
    await user.click(within(detail).getByRole("button", { name: "Replay to authz.end_delegations_of_deactivated" }));
    const dialog = await screen.findByRole("dialog", { name: "Replay the event?" });
    await user.click(within(dialog).getByRole("button", { name: "Replay" }));
    expect(calls.some((call) => call.key === "POST /api/v1/events/replay")).toBe(false);
    await fill(user, within(dialog).getByLabelText(/^Reason/), "The delegation stayed open: ticket 12.");
    await user.click(within(dialog).getByRole("button", { name: "Replay" }));

    await waitFor(() => expect(calls.some((call) => call.key === "POST /api/v1/events/replay")).toBe(true));
    const replay = calls.find((call) => call.key === "POST /api/v1/events/replay");
    expect(JSON.parse(replay?.init?.body as string)).toEqual({
      subscriber: "authz.end_delegations_of_deactivated",
      event_ids: [deactivated.id],
      reason: "The delegation stayed open: ticket 12.",
    });
    expect(
      await screen.findByText(
        "1 event went to authz.end_delegations_of_deactivated: 1 to be tried again, 0 given for the first time, 0 already handled.",
      ),
    ).toBeInTheDocument();
  });

  it("reports an event past the retention as archived, with what to do [TF-112 AC2, ADR-0060]", async () => {
    stubFetch(
      handlers(["events.events.read"], {
        [`GET /api/v1/events/${deactivated.id}`]: () =>
          new Response(
            JSON.stringify({
              status: 404,
              title: "Not found",
              detail:
                "This event is not in the outbox any more: the events every subscriber handled were archived to the tenant's container up to 2026-10-01 00:00:00Z, after the retention. Ask the platform operator for the archive if you need it.",
              error_code: "events.event.archived",
              correlation_id: "c-1",
            }),
            { status: 404, headers: { "Content-Type": "application/problem+json" } },
          ),
      }),
    );
    renderWithProviders(<EventsPage />, { route: `/console/events?event=${deactivated.id}` });

    expect(await screen.findByRole("alert")).toHaveTextContent("archived to the tenant's container up to 2026-10-01");
  });

  it("searches by event, record, correlation identifier and days in the list grammar [TF-112 AC1, ADR-0060]", () => {
    expect(
      eventFilterQuery({
        type: "identity.person.deactivated",
        recordId: " 0198f0c2-0000-7000-8000-000000000001 ",
        correlationId: "0HN7Q1",
        from: "",
        to: "",
      }),
    ).toEqual([
      "filter=type%3Aeq%3Aidentity.person.deactivated",
      "filter=record_id%3Aeq%3A0198f0c2-0000-7000-8000-000000000001",
      "filter=correlation_id%3Aeq%3A0HN7Q1",
    ]);
    expect(
      eventFilterQuery({ type: "", recordId: "", correlationId: "", from: "2026-10-30", to: "2026-10-31" }),
    ).toHaveLength(2);
  });
});
