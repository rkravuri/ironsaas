import type { FormErrors } from "@mantine/form";
import type { z } from "zod";

/**
 * Mantine's validate function from a Zod schema: the first message per field, keyed by the form's path (dots for
 * nesting and array indexes, as Mantine reads them). Ten lines beat a resolver package (ADR-0027).
 */
export function zodValidator<Values>(schema: z.ZodType<Values>): (values: Values) => FormErrors {
  return (values) => {
    const result = schema.safeParse(values);
    if (result.success) {
      return {};
    }
    const errors: FormErrors = {};
    for (const issue of result.error.issues) {
      const path = issue.path.map(String).join(".");
      if (path !== "" && errors[path] === undefined) {
        errors[path] = issue.message;
      }
    }
    return errors;
  };
}
