import { useForm, type UseFormReturnType } from "@mantine/form";
import type { z } from "zod";
import { zodValidator } from "./zod";

/** Mantine's form with a Zod schema: inline validation after the first blur, the form's own values as the truth. */
export function useZodForm<Values extends Record<string, unknown>>(options: {
  schema: z.ZodType<Values>;
  initialValues: Values;
}): UseFormReturnType<Values> {
  return useForm<Values>({
    initialValues: options.initialValues,
    validate: zodValidator(options.schema),
    validateInputOnBlur: true,
  });
}
