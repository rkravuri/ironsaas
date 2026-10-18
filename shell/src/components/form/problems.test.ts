import { act, renderHook } from "@testing-library/react";
import { useForm } from "@mantine/form";
import { describe, expect, it } from "vitest";
import { ApiError, ProblemSchema } from "@/api/client";
import { applyProblem, fieldPath } from "./problems";

// A problem exactly as ValidationFilter and ProblemFactory write it (tests/Kernel/Iron.Kernel.Api.Tests/ProblemTests.cs).
const body = {
  type: "urn:iron:error:api.validation.failed",
  title: "The request is not valid.",
  status: 400,
  detail: "The request is not valid.",
  instance: "/api/v1/things",
  error_code: "api.validation.failed",
  correlation_id: "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
  errors: [
    { field: "display_name", code: "not_empty", message: "Enter a display name." },
    { field: "display_name", code: "maximum_length", message: "Use at most 80 characters." },
    { field: "items[0].unit_price", code: "greater_than", message: "The price must be above zero." },
    { field: "tenant", code: "not_found", message: "The tenant does not exist." },
  ],
};

describe("fieldPath", () => {
  it("turns the API's snake_case path into the form's camelCase path, indexes included", () => {
    expect(fieldPath("display_name")).toBe("displayName");
    expect(fieldPath("items[0].unit_price")).toBe("items.0.unitPrice");
    expect(fieldPath("address.postal_code")).toBe("address.postalCode");
    expect(fieldPath("name")).toBe("name");
  });
});

describe("applyProblem", () => {
  it("reads the problem as the API writes it and puts each field's messages on its field", () => {
    const problem = ProblemSchema.parse(body);
    const error = new ApiError(400, problem, undefined);
    const { result } = renderHook(() => useForm({ initialValues: { displayName: "", items: [{ unitPrice: 0 }] } }));

    let summary: ReturnType<typeof applyProblem> | undefined;
    act(() => {
      summary = applyProblem(result.current, error);
    });

    expect(result.current.errors.displayName).toBe("Enter a display name. Use at most 80 characters.");
    expect(result.current.errors["items.0.unitPrice"]).toBe("The price must be above zero.");
    expect(summary?.unmapped.map((problem) => problem.field)).toEqual(["tenant"]);
    expect(summary?.message).toBe("The tenant does not exist.");
    expect(summary?.correlationId).toBe("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
  });

  it("leaves the top empty when every problem found its field, and shows the problem itself when none did", () => {
    const fielded = new ApiError(400, ProblemSchema.parse({ ...body, errors: body.errors.slice(0, 1) }), undefined);
    const { result } = renderHook(() => useForm({ initialValues: { displayName: "" } }));
    let summary: ReturnType<typeof applyProblem> | undefined;
    act(() => {
      summary = applyProblem(result.current, fielded);
    });
    expect(summary?.message).toBe("");

    const bare = new ApiError(409, ProblemSchema.parse({ status: 409, detail: "The login id is taken.", error_code: "identity.users.conflict", correlation_id: "c" }), undefined);
    act(() => {
      summary = applyProblem(result.current, bare);
    });
    expect(summary?.message).toBe("The login id is taken.");
    expect(summary?.correlationId).toBe("c");
  });
});
