import { afterEach, describe, expect, it } from "vitest";
import { setTimeZone } from "@/i18n/dates";
import { emptyFilters, endOfDay, filterQuery, filtersFromLink, fromAnAlert, signedInByTerm } from "./audit-filters";

afterEach(() => {
  setTimeZone(undefined);
});

describe("the audit search's filters", () => {
  it("search by who, who signed in as them, which record, which type and which days, the days in the person's zone [TF-064 AC1, TF-065 AC2, ADR-0050]", () => {
    setTimeZone("Asia/Kolkata");
    const terms = filterQuery({
      ...emptyFilters,
      eventType: " identity.user.invited ",
      actorId: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
      signedInBy: "impersonator_id:01a14e12-0000-7000-8000-0000000000f1",
      recordType: "principal",
      recordId: "01a14e12-0000-7000-8000-000000000009",
      source: "ui",
      from: "2026-10-22",
      to: "2026-10-22",
    }).map((term) => decodeURIComponent(term));

    expect(terms).toEqual([
      "filter=event_type:eq:identity.user.invited",
      "filter=actor_id:eq:01a14e12-f3a8-7155-b5b6-17c3abceae77",
      "filter=impersonator_id:eq:01a14e12-0000-7000-8000-0000000000f1",
      "filter=record_type:eq:principal",
      "filter=record_id:eq:01a14e12-0000-7000-8000-000000000009",
      "filter=source:eq:ui",
      "filter=created_at:gte:2026-10-21T18:30:00.000Z",
      "filter=created_at:lt:2026-10-22T18:30:00.000Z",
    ]);
    expect(filterQuery(emptyFilters)).toEqual([]);
  });

  it("open a security alert's link on the search of its events: its types, its group and its first and last moments, both included [ADR-0056]", () => {
    const byPerson = filtersFromLink(
      new URLSearchParams(
        "event_type=audit.events_exported,authz.roles_exported&actor_id=0198f0c2-0000-7000-8000-000000000001&since=2026-10-29T10%3A00%3A00.000000Z&until=2026-10-29T10%3A08%3A15.000000Z",
      ),
    );
    const byAddress = filtersFromLink(new URLSearchParams("event_type=identity.login_failed&address=203.0.113.7"));
    const byLogin = filtersFromLink(new URLSearchParams("event_type=identity.login_locked&target=ana%40acme.test"));

    expect(filterQuery(byPerson).map(decodeURIComponent)).toEqual([
      "filter=event_type:in:audit.events_exported,authz.roles_exported",
      "filter=actor_id:eq:0198f0c2-0000-7000-8000-000000000001",
      "filter=created_at:gte:2026-10-29T10:00:00.000000Z",
      "filter=created_at:lte:2026-10-29T10:08:15.000000Z",
    ]);
    expect(filterQuery(byAddress).map(decodeURIComponent)).toEqual([
      "filter=event_type:eq:identity.login_failed",
      "address=203.0.113.7",
    ]);
    expect(filterQuery(byLogin).map(decodeURIComponent)).toEqual([
      "filter=event_type:eq:identity.login_locked",
      "filter=target:eq:ana@acme.test",
    ]);
    expect([fromAnAlert(byPerson), fromAnAlert(byAddress), fromAnAlert(emptyFilters)]).toEqual([true, true, false]);
  });

  it("find a break-glass account's events as the actor's, since it acts as itself [TF-025 AC2, ADR-0049, ADR-0050]", () => {
    const account = "01a14e12-0000-7000-8000-0000000000b9";
    expect(filterQuery({ ...emptyFilters, signedInBy: `actor_id:${account}` }).map(decodeURIComponent)).toEqual([
      `filter=actor_id:eq:${account}`,
    ]);
    expect(signedInByTerm("anything")).toBeUndefined();
  });

  it("end a searched day at the next midnight in the person's zone, 25 hours after its start when the clocks go back [ADR-0050]", () => {
    setTimeZone("Europe/London");
    expect(endOfDay("2026-10-25").toISOString()).toBe("2026-10-26T00:00:00.000Z");
    expect(endOfDay("2026-12-31").toISOString()).toBe("2027-01-01T00:00:00.000Z");
  });
});
