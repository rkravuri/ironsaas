import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";
import { Alert, Anchor, Button, Container, Paper, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { useForm } from "@mantine/form";
import { ApiError } from "@/api/client";
import { acceptInvitation } from "@/api/identity";
import { PasswordRules } from "./password";
import { failedRule, usePasswordPolicy, type PasswordRule } from "./password-policy";

interface InvitationFormValues {
  displayName: string;
  password: string;
  confirm: string;
}

type InvitationState =
  | { kind: "form"; failed?: PasswordRule | undefined }
  | { kind: "done" }
  | { kind: "error"; message: string; correlationId: string | undefined };

/**
 * The page behind an invitation link: /invitations/{token}. The invitee sets a password under the tenant's policy, shown
 * in words before they type (TF-012, TF-018); a password the API refuses keeps the form and names the rule it failed.
 */
export function InvitationPage() {
  const { t } = useTranslation();
  const { token = "" } = useParams();
  const policy = usePasswordPolicy();
  const [state, setState] = useState<InvitationState>({ kind: "form" });
  const [submitting, setSubmitting] = useState(false);
  const form = useForm<InvitationFormValues>({
    initialValues: { displayName: "", password: "", confirm: "" },
    validate: {
      password: (value) =>
        value.length < policy.minimum_length ? t("password.tooShort", { min: policy.minimum_length }) : null,
      confirm: (value, values) => (value !== values.password ? t("password.mismatch") : null),
    },
  });

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    try {
      await acceptInvitation(token, values.password, values.displayName.trim() || undefined);
      setState({ kind: "done" });
    } catch (error) {
      const rule = failedRule(error);
      if (rule !== undefined && error instanceof ApiError) {
        form.setFieldError(
          "password",
          error.errors?.find((problem) => problem.field === "password")?.message ?? error.message,
        );
        setState({ kind: "form", failed: rule });
      } else if (error instanceof ApiError) {
        setState({
          kind: "error",
          message:
            error.status === 404 || error.status === 409 || error.errorCode === "identity.invitation.expired"
              ? t("invitation.invalid")
              : error.message,
          correlationId: error.correlationId,
        });
      } else {
        setState({ kind: "error", message: t("errors.unexpected"), correlationId: undefined });
      }
    } finally {
      setSubmitting(false);
    }
  });

  return (
    <Container size={460} py="xl">
      <Title order={2} ta="center" mb="lg">
        {t("invitation.title")}
      </Title>
      <Paper withBorder shadow="sm" p="lg" radius="md">
        {state.kind === "done" ? (
          <Stack>
            <Alert color="success">{t("invitation.done")}</Alert>
            <Anchor component={Link} to="/login">
              {t("login.title")}
            </Anchor>
          </Stack>
        ) : (
          <form onSubmit={submit} noValidate>
            <Stack>
              <Text size="sm">{t("invitation.intro", { hostname: window.location.hostname })}</Text>
              <TextInput
                label={t("invitation.displayName")}
                autoComplete="name"
                {...form.getInputProps("displayName")}
              />
              <PasswordRules
                policy={policy}
                withHistory={false}
                failed={state.kind === "form" ? state.failed : undefined}
              />
              <PasswordInput
                label={t("invitation.password")}
                autoComplete="new-password"
                required
                {...form.getInputProps("password")}
              />
              <PasswordInput
                label={t("invitation.confirm")}
                autoComplete="new-password"
                required
                {...form.getInputProps("confirm")}
              />
              {state.kind === "error" && (
                <Alert color="danger" role="alert">
                  {state.message}
                  {state.correlationId && (
                    <Text size="xs" c="dimmed" mt={4}>
                      {t("errors.correlation", { id: state.correlationId })}
                    </Text>
                  )}
                </Alert>
              )}
              <Button type="submit" loading={submitting} fullWidth>
                {t("invitation.submit")}
              </Button>
            </Stack>
          </form>
        )}
      </Paper>
    </Container>
  );
}
