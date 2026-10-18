import type { UseFormReturnType } from "@mantine/form";
import type { ApiError, FieldProblem } from "@/api/client";

/** What is left for the top of the form after the field problems found their fields. */
export interface ProblemSummary {
  /** Empty when every problem landed on a field. */
  message: string;
  correlationId: string | undefined;
  unmapped: readonly FieldProblem[];
}

/** The API's path to the form's: items[0].unit_price becomes items.0.unitPrice (ADR-0027). */
export function fieldPath(apiField: string): string {
  return apiField
    .split(".")
    .map((segment) => segment.replace(/\[(\d+)\]/g, ".$1").replace(/_([a-z0-9])/g, (_match, letter: string) => letter.toUpperCase()))
    .join(".");
}

/**
 * Puts an ApiError's field problems on the form's fields (a field is known when its first path segment is a value of
 * the form) and returns what found no field, with the correlation id, for the top of the form.
 */
export function applyProblem<Values extends Record<string, unknown>>(form: UseFormReturnType<Values>, error: ApiError): ProblemSummary {
  const values = form.getValues();
  const unmapped: FieldProblem[] = [];
  const mapped = new Map<string, string[]>();
  for (const problem of error.errors ?? []) {
    const path = fieldPath(problem.field);
    const head = path.split(".")[0] ?? "";
    if (head !== "" && Object.hasOwn(values, head)) {
      mapped.set(path, [...(mapped.get(path) ?? []), problem.message]);
    } else {
      unmapped.push(problem);
    }
  }
  for (const [path, messages] of mapped) {
    form.setFieldError(path, messages.join(" "));
  }
  const message = unmapped.length > 0 ? unmapped.map((problem) => problem.message).join(" ") : mapped.size > 0 ? "" : error.message;
  return { message, correlationId: error.correlationId, unmapped };
}
