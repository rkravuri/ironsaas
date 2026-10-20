import { useTranslation } from "react-i18next";
import { Checkbox, Stack, Text, TextInput, Title } from "@mantine/core";
import { z } from "zod";
import { ApiError, ProblemSchema } from "@/api/client";
import { Form, useZodForm } from "@/components/form";

const schema = z.object({
  displayName: z.string().min(1, "Enter a display name.").max(80, "Use at most 80 characters."),
  email: z.string().email("Enter an email address."),
  likeTheServer: z.boolean(),
});
type Values = z.infer<typeof schema>;

/** The problem the API would write for a taken email and a field the form does not have (ProblemFactory's shape). */
const serverProblem = () =>
  new ApiError(
    400,
    ProblemSchema.parse({
      status: 400,
      error_code: "api.validation.failed",
      correlation_id: "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
      errors: [
        { field: "email", code: "taken", message: "This email is already used by another account." },
        { field: "tenant", code: "not_found", message: "The tenant does not exist." },
      ],
    }),
    undefined,
  );

/** The form: inline validation after the first blur, and the server's answer landing on its field. */
export function FormSection() {
  const { t } = useTranslation();
  const form = useZodForm<Values>({ schema, initialValues: { displayName: "", email: "", likeTheServer: false } });
  return (
    <section aria-labelledby="gallery-form">
      <Stack gap="lg">
        <Title order={2} id="gallery-form">
          {t("gallery.sections.form")}
        </Title>
        <Text>{t("gallery.form.intro")}</Text>
        <Form
          form={form}
          onSubmit={(values) => (values.likeTheServer ? Promise.reject(serverProblem()) : Promise.resolve())}
          submitLabel={t("gallery.form.save")}
          secondary={{ label: t("gallery.form.cancel"), onClick: () => form.reset() }}
        >
          <TextInput
            label={t("gallery.form.displayName")}
            description={t("gallery.form.displayNameHint")}
            {...form.getInputProps("displayName")}
          />
          <TextInput label={t("gallery.form.email")} {...form.getInputProps("email")} />
          <Checkbox
            label={t("gallery.form.likeTheServer")}
            {...form.getInputProps("likeTheServer", { type: "checkbox" })}
          />
        </Form>
      </Stack>
    </section>
  );
}
