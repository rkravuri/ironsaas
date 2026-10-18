import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";
import { Alert, Button, Container, Paper, PasswordInput, Stack, Text, TextInput, Title } from "@mantine/core";
import { useForm } from "@mantine/form";
import { ApiError } from "@/api/client";
import { login } from "@/api/identity";
import { useSession } from "./session-context";

interface LoginFormValues {
  loginId: string;
  password: string;
}

/** The fixed message on failure is the API's (TF-030): the page never says which of the two was wrong. */
export function LoginPage() {
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

  const returnTo = (location.state as { from?: string } | null)?.from ?? "/";

  const submit = form.onSubmit(async (values) => {
    setSubmitting(true);
    setProblem(undefined);
    try {
      await login(values.loginId.trim(), values.password);
      await session.refresh();
      void navigate(returnTo, { replace: true });
    } catch (error) {
      if (error instanceof ApiError) {
        setProblem({ message: error.status === 401 || error.status === 400 ? t("login.failed") : error.message, correlationId: error.correlationId });
      } else {
        setProblem({ message: t("errors.unexpected"), correlationId: undefined });
      }
    } finally {
      setSubmitting(false);
    }
  });

  return (
    <Container size={420} py="xl">
      <Title order={2} ta="center" mb="lg">
        {t("login.title")}
      </Title>
      <Text c="dimmed" size="sm" ta="center" mb="md">
        {t("app.tenant", { hostname: window.location.hostname })}
      </Text>
      <Paper withBorder shadow="sm" p="lg" radius="md">
        <form onSubmit={submit} noValidate>
          <Stack>
            <TextInput label={t("login.loginId")} autoComplete="username" autoFocus required {...form.getInputProps("loginId")} />
            <PasswordInput label={t("login.password")} autoComplete="current-password" required {...form.getInputProps("password")} />
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
          </Stack>
        </form>
      </Paper>
    </Container>
  );
}
