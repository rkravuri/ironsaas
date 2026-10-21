import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";
import {
  Alert,
  Box,
  Button,
  Container,
  Group,
  Paper,
  PasswordInput,
  Stack,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import { useForm } from "@mantine/form";
import { ApiError } from "@/api/client";
import { login } from "@/api/identity";
import type { RecoveryState } from "./RecoveryPage";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import type { LoginState } from "@/shell/Guards";
import { useSession } from "./session-context";

interface LoginFormValues {
  loginId: string;
  password: string;
}

interface LoginPageProps {
  /** Off in the gallery, where a focused field would steal the page. */
  autoFocus?: boolean;
}

/**
 * One fixed message for every failure, the API's own (TF-011 AC2): the page never says whether the account exists, which
 * of the two was wrong, or whether the account is active; the correlation id goes with it when the API gave one. A login
 * locked after too many failures has its own message, which is the same for every identifier, known or not (TF-012,
 * ADR-0046). A right password past its maximum age goes on to the recovery page with the code the API gave. Until email
 * exists (S4), "Forgot your password?" says to ask the administrator for a recovery link.
 */
export function LoginPage({ autoFocus = true }: LoginPageProps = {}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const session = useSession();
  const [problem, setProblem] = useState<{ message: string; correlationId: string | undefined } | undefined>();
  const [submitting, setSubmitting] = useState(false);
  const form = useForm<LoginFormValues>({
    initialValues: { loginId: "", password: "" },
    validate: {
      loginId: (value) => (value.trim().length === 0 ? t("login.required") : null),
      password: (value) => (value.length === 0 ? t("login.required") : null),
    },
  });
  const hostname = window.location.hostname;

  const state = location.state as LoginState | null;
  const returnTo = state?.from ?? "/";
  const reason = state?.reason;

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    setProblem(undefined);
    try {
      await login(values.loginId.trim(), values.password);
      await session.signedIn();
      void navigate(returnTo, { replace: true });
    } catch (error) {
      if (error instanceof ApiError && error.errorCode === "identity.password.expired" && error.recoveryToken) {
        const state: RecoveryState = { expired: true };
        void navigate(`/recovery/${encodeURIComponent(error.recoveryToken)}`, { state });
        return;
      }
      setProblem({
        message:
          error instanceof ApiError && error.errorCode === "identity.login.locked"
            ? t("login.locked")
            : t("login.failed"),
        correlationId: error instanceof ApiError ? error.correlationId : undefined,
      });
    } finally {
      setSubmitting(false);
    }
  });

  return (
    <Container size={420} py="xl">
      <Stack align="center" gap="xs" mb="md">
        <Welcome decorative size="md" />
        <Group gap="sm" justify="center" wrap="nowrap">
          <TenantMark hostname={hostname} size={32} />
          <Title order={2} variant="display">
            {t("login.title")}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" ta="center">
          {t("app.tenant", { hostname })}
        </Text>
      </Stack>
      {reason !== undefined && (
        <Alert color="info" role="status" mb="md" data-reason={reason}>
          {t(`login.ended.${reason}`)}
        </Alert>
      )}
      {/* The mark as a texture: behind the opaque card at 7 percent, so it never sits under text (brief section 4). */}
      <Box pos="relative">
        <TenantMark
          hostname={hostname}
          size={400}
          opacity={0.07}
          style={{ position: "absolute", left: "50%", top: 0, transform: "translateX(-50%)", pointerEvents: "none" }}
        />
        <Paper withBorder shadow="sm" p="lg" pos="relative">
          <form onSubmit={submit} noValidate>
            <Stack>
              <TextInput
                label={t("login.loginId")}
                autoComplete="username"
                autoFocus={autoFocus}
                required
                {...form.getInputProps("loginId")}
              />
              <PasswordInput
                label={t("login.password")}
                autoComplete="current-password"
                required
                {...form.getInputProps("password")}
              />
              {problem && (
                <Alert color="danger" role="alert">
                  {problem.message}
                  {problem.correlationId && (
                    <Text size="xs" c="dimmed" mt={4}>
                      {t("errors.correlation", { id: problem.correlationId })}
                    </Text>
                  )}
                </Alert>
              )}
              <Button type="submit" loading={submitting} fullWidth>
                {t("login.submit")}
              </Button>
              <Text size="sm" data-forgot>
                <Text span inherit fw={600}>
                  {t("login.forgot.title")}
                </Text>{" "}
                <Text span inherit c="dimmed">
                  {t("login.forgot.text")}
                </Text>
              </Text>
            </Stack>
          </form>
        </Paper>
      </Box>
    </Container>
  );
}
