import { afterEach, describe, expect, it, vi } from "vitest";
import { z } from "zod";
import {
  ApiError,
  listSchema,
  onChangeHeld,
  onEnrolmentRequired,
  onReauthenticationRequired,
  onResponse,
  onUnauthenticated,
  request,
  type ResponseInfo,
} from "./client";
import { json, problem, stubFetch } from "@/test/render";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the API client", () => {
  it("sends JSON with the session cookie and validates the answer [ADR-0020]", async () => {
    const { calls } = stubFetch({ "POST /auth/login": () => json({ ok: true }) });

    const result = await request("/auth/login", {
      method: "POST",
      body: { login_id: "a", password: "b" },
      schema: z.object({ ok: z.boolean() }),
    });

    expect(result).toEqual({ ok: true });
    expect(calls[0]?.init?.credentials).toBe("same-origin");
    expect(calls[0]?.init?.body).toBe(JSON.stringify({ login_id: "a", password: "b" }));
  });

  it("turns a problem into an ApiError with the error code and the correlation id [ADR-0020]", async () => {
    stubFetch({
      "GET /api/v1/users": () =>
        problem(
          400,
          { title: "Bad Request", detail: "Unknown field 'nope'.", error_code: "list.unknown_field" },
          "abc123",
        ),
    });

    const error = await request("/api/v1/users").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.errorCode).toBe("list.unknown_field");
    expect(apiError.correlationId).toBe("abc123");
    expect(apiError.message).toBe("Unknown field 'nope'.");
  });

  it("reads the list envelope as Iron.Kernel.Api writes it [ADR-0009, ADR-0020]", async () => {
    stubFetch({
      "GET /api/v1/things": () =>
        json({
          data: [{ id: "1" }],
          meta: {
            page_number: 1,
            page_size: 50,
            total_count: 1,
            total_pages: 1,
            has_previous: false,
            has_next: false,
            page_size_capped: false,
            max_page_size: 200,
          },
          links: {
            self: "/api/v1/things?page_number=1",
            first: "/api/v1/things?page_number=1",
            previous: null,
            next: null,
            last: "/api/v1/things?page_number=1",
          },
        }),
    });

    const list = await request("/api/v1/things", { schema: listSchema(z.object({ id: z.string() })) });

    expect(list.data).toEqual([{ id: "1" }]);
    expect(list.meta.total_count).toBe(1);
    expect(list.links.next).toBeNull();
  });

  it("takes the correlation id from the header when the body has none, and tells listeners about a 401 [ADR-0020]", async () => {
    stubFetch({
      "GET /api/v1/me": () => new Response("", { status: 401, headers: { "X-Correlation-Id": "from-header" } }),
    });
    const listener = vi.fn();
    const stop = onUnauthenticated(listener);

    const error = (await request("/api/v1/me").catch((e: unknown) => e)) as ApiError;
    stop();

    expect(error.isUnauthenticated).toBe(true);
    expect(error.correlationId).toBe("from-header");
    expect(listener).toHaveBeenCalledOnce();
    expect(listener).toHaveBeenCalledWith(error);
  });

  it("tells the session clock about every answer, with the server's clock from the Date header [ADR-0033]", async () => {
    stubFetch({
      "GET /api/v1/users": () => json({}, 200, { Date: "Mon, 19 Oct 2026 08:00:00 GMT" }),
      "GET /api/v1/roles": () => problem(403, { title: "Forbidden", error_code: "authz.forbidden" }),
    });
    const seen: ResponseInfo[] = [];
    const stop = onResponse((info) => seen.push(info));

    await request("/api/v1/users");
    await request("/api/v1/roles").catch(() => undefined);
    stop();

    expect(seen.map((info) => [info.path, info.status])).toEqual([
      ["/api/v1/users", 200],
      ["/api/v1/roles", 403],
    ]);
    expect(seen[0]?.serverDate).toBe(Date.parse("2026-10-19T08:00:00Z"));
    expect(seen[1]?.serverDate).toBeUndefined();
    expect(seen[0]?.receivedAt).toBeGreaterThan(0);
  });

  it("sends a request refused for an old sign-in again once the person confirmed it is them [TF-248 AC5]", async () => {
    let sent = 0;
    stubFetch({
      "POST /api/v1/users/u1/deactivate": () =>
        ++sent === 1 ? problem(403, { error_code: "auth.reauthentication_required" }) : json({ ok: true }),
    });
    const confirm = vi.fn(() => Promise.resolve(true));
    const stop = onReauthenticationRequired(confirm);

    try {
      await expect(request("/api/v1/users/u1/deactivate", { method: "POST" })).resolves.toEqual({ ok: true });
    } finally {
      stop();
    }

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(sent).toBe(2);
  });

  it("lets the refusal through, sending nothing again, when the person cancels or no dialog is there [TF-248 AC5]", async () => {
    let sent = 0;
    stubFetch({
      "POST /api/v1/users/u1/deactivate": () => {
        sent++;
        return problem(403, { error_code: "auth.reauthentication_required" });
      },
    });
    const stop = onReauthenticationRequired(() => Promise.resolve(false));

    const cancelled = await request("/api/v1/users/u1/deactivate", { method: "POST" }).catch((error: unknown) => error);
    stop();
    const unanswered = await request("/api/v1/users/u1/deactivate", { method: "POST" }).catch(
      (error: unknown) => error,
    );

    expect(cancelled).toBeInstanceOf(ApiError);
    expect((cancelled as ApiError).needsReauthentication).toBe(true);
    expect(unanswered).toBeInstanceOf(ApiError);
    expect(sent).toBe(2);
  });

  it("tells the frame when a call is refused because the tenant now requires a second factor of the person [TF-013 AC1, ADR-0047]", async () => {
    stubFetch({ "GET /api/v1/users": () => problem(403, { error_code: "identity.mfa.enrolment_required" }) });
    const heard = vi.fn();
    const stop = onEnrolmentRequired(heard);

    const refused = await request("/api/v1/users").catch((error: unknown) => error);
    stop();

    expect(heard).toHaveBeenCalledTimes(1);
    expect((refused as ApiError).status).toBe(403); // the session stays: a 403, not a 401
  });

  it("throws a change held for a second administrator, with its id, and tells the frame, while a 202 with its resource is an answer [TF-068 AC1, ADR-0052]", async () => {
    stubFetch({
      "PUT /api/v1/settings/tenant/identity.password.min_length": () =>
        problem(202, { error_code: "changes.change.held", change_id: "c-1", expires_at: "2026-10-30T10:00:00Z" }),
      "POST /api/v1/evidence-packs": () => json({ id: "p-1" }, 202),
    });
    const heard = vi.fn();
    const stop = onChangeHeld(heard);

    const held = await request("/api/v1/settings/tenant/identity.password.min_length", {
      method: "PUT",
      body: { value: 14 },
    }).catch((error: unknown) => error);
    const accepted = await request("/api/v1/evidence-packs", { method: "POST", body: {} });
    stop();

    expect(held).toBeInstanceOf(ApiError);
    expect((held as ApiError).isHeld).toBe(true);
    expect((held as ApiError).changeId).toBe("c-1");
    expect(heard).toHaveBeenCalledTimes(1);
    expect(accepted).toEqual({ id: "p-1" });
  });
});
