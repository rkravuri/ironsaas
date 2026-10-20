import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";
import { Alert, Anchor, Button, Container, Paper, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { useForm } from "@mantine/form";
import { ApiError } from "@/api/client";
import { acceptInvitation } from "@/api/identity";

const minimumLength = 12;

interface InvitationFormValues {
  displayName: string;
  password: string;
  confirm: string;
}

/** The page behind the link the fleet prints: /invitations/{token}. Sets the first administrator's password (TF-030). */
export function InvitationPage() {
  const { t } = useTranslation();
  const { token = "" } = useParams();
  const [state, setState] = useState<
    { kind: "form" } | { kind: "done" } | { kind: "error"; message: string; correlationId: string | undefined }
  >({ kind: "form" });
  const [submitting, setSubmitting] = useState(false);
  const form = useForm<InvitationFormValues>({
    initialValues: { displayName: "", password: "", confirm: "" },
    validate: {
      password: (value) => (value.length < minimumLength ? t("invitation.tooShort", { min: minimumLength }) : null),
      confirm: (value, values) => (value !== values.password ? t("invitation.mismatch") : null),
    },
  });

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    try {
      await acceptInvitation(token, values.password, values.displayName.trim() || undefined);
      setState({ kind: "done" });
    } catch (error) {
      if (error instanceof ApiError) {
        setState({
          kind: "error",
          message:
            error.status === 404 || error.status === 400 || error.status === 409
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
