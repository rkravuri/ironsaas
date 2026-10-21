import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useLocation } from "react-router";
import { Alert, Anchor, Button, Container, Paper, PasswordInput, Stack, Text, Title } from "@mantine/core";
import { useForm } from "@mantine/form";
import { ApiError } from "@/api/client";
import { recoverPassword } from "@/api/identity";
import { PasswordRules } from "./password";
import { failedRule, usePasswordPolicy, type PasswordRule } from "./password-policy";

interface RecoveryFormValues {
  password: string;
  confirm: string;
}

/** What the sign-in page passes on when the password was right but too old (TF-012). */
export interface RecoveryState {
  expired?: boolean;
}

type PageState =
  | { kind: "form"; failed?: PasswordRule | undefined }
  | { kind: "done" }
  | { kind: "error"; message: string; correlationId: string | undefined };

/**
 * The page behind a recovery link: /recovery#{code} (TF-017, ADR-0046). The code is in the fragment, which the browser
 * never sends to a server, and goes to the API in the body. An administrator or the operator issued the
 * link, or the sign-in found the password expired. The new password follows the tenant's policy, in words before the
 * person types; saving it ends every session of the account. A link works once, for 15 minutes: one that does not work
 * any more says to ask the administrator, since a password refused by the policy leaves the link open.
 */
export function RecoveryPage() {
  const { t } = useTranslation();
  const location = useLocation();
  const token = location.hash.slice(1);
  const expired = (location.state as RecoveryState | null)?.expired === true;
  const policy = usePasswordPolicy();
  const [state, setState] = useState<PageState>({ kind: "form" });
  const [submitting, setSubmitting] = useState(false);
  const form = useForm<RecoveryFormValues>({
    initialValues: { password: "", confirm: "" },
    validate: {
      password: (value) =>
        value.length < policy.minimum_length ? t("password.tooShort", { min: policy.minimum_length }) : null,
      confirm: (value, values) => (value !== values.password ? t("password.mismatch") : null),
    },
  });

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    try {
      await recoverPassword(token, values.password);
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
            error.errorCode === "identity.recovery.invalid"
              ? t("recovery.invalid")
              : error.errorCode === "identity.recovery.not_allowed"
                ? t("recovery.notAllowed")
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
        {t("recovery.title")}
      </Title>
      <Paper withBorder shadow="sm" p="lg" radius="md">
        {state.kind === "done" ? (
          <Stack>
            <Alert color="success">{t("recovery.done")}</Alert>
            <Anchor component={Link} to="/login">
              {t("login.title")}
            </Anchor>
          </Stack>
        ) : (
          <form onSubmit={submit} noValidate>
            <Stack>
              {expired && (
                <Alert color="info" role="status">
                  {t("recovery.expired")}
                </Alert>
              )}
              <Text size="sm">{t("recovery.intro", { hostname: window.location.hostname })}</Text>
              <PasswordRules policy={policy} withHistory failed={state.kind === "form" ? state.failed : undefined} />
              <PasswordInput
                label={t("recovery.password")}
                autoComplete="new-password"
                required
                {...form.getInputProps("password")}
              />
              <PasswordInput
                label={t("recovery.confirm")}
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
                {t("recovery.submit")}
              </Button>
            </Stack>
          </form>
        )}
      </Paper>
    </Container>
  );
}
