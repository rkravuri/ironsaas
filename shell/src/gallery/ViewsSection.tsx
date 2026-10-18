import { useTranslation } from "react-i18next";
import { Paper, Stack, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { LoginPage } from "@/auth/LoginPage";
import { ErrorView } from "@/shell/ErrorBoundary";
import { LoadingView } from "@/shell/Guards";

// The shapes the real views receive: a 403 problem from the API, a 404 from the router, an error from code.
const forbidden = new ApiError(403, { title: "Forbidden", error_code: "authz.forbidden", correlation_id: "01a14e12-0000-7000-8000-0000000000aa" }, undefined);
const notFound = { status: 404, statusText: "Not Found", internal: false, data: null };

/** The error views, the loading view and the login form, as the shell shows them. */
export function ViewsSection() {
  const { t } = useTranslation();

  return (
    <section aria-labelledby="gallery-views">
      <Stack gap="lg">
        <Title order={2} id="gallery-views">
          {t("gallery.sections.views")}
        </Title>
        <Title order={3}>{t("gallery.views.forbidden")}</Title>
        <Paper withBorder>
          <ErrorView error={forbidden} />
        </Paper>
        <Title order={3}>{t("gallery.views.notFound")}</Title>
        <Paper withBorder>
          <ErrorView error={notFound} onRetry={() => undefined} />
        </Paper>
        <Title order={3}>{t("gallery.views.unexpected")}</Title>
        <Paper withBorder>
          <ErrorView error={new Error(t("gallery.views.brokenMessage"))} onRetry={() => undefined} />
        </Paper>
        <Title order={3}>{t("gallery.views.loading")}</Title>
        <Paper withBorder>
          <LoadingView height={160} />
        </Paper>
        <Title order={3}>{t("gallery.views.login")}</Title>
        <Paper withBorder>
          <LoginPage autoFocus={false} />
        </Paper>
      </Stack>
    </section>
  );
}
