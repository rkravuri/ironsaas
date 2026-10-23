import { afterEach, describe, expect, it } from "vitest";
import { setTimeZone } from "@/i18n/dates";
import { emptyFilters, endOfDay, filterQuery, signedInByTerm } from "./audit-filters";

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
      "filter=created_at:ge:2026-10-21T18:30:00.000Z",
      "filter=created_at:lt:2026-10-22T18:30:00.000Z",
    ]);
    expect(filterQuery(emptyFilters)).toEqual([]);
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
