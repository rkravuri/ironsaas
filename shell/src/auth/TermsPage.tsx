import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Navigate, useLocation } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Badge, Button, Container, Group, Paper, ScrollArea, Stack, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { acceptTerms, declineTerms, fetchMyTerms, myTermsQueryKey, type Document } from "@/api/identity";
import { ConfirmDialog, LoadingState } from "@/components/panels";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import type { EnrolmentState } from "@/shell/Guards";
import { useSession } from "./session-context";
import { formatDate } from "@/i18n/dates";

/**
 * Terms acceptance (TF-028, ADR-0049): a new version of a document the person must accept, the platform's or their
 * organisation's, and every page waits for it. The person reads each, then accepts them all, which records the versions
 * with the time, and goes back to the page they were on; or declines, which signs them out, and they are asked again at
 * their next sign-in. Someone signed in as the person never reaches this page: it is the person's alone.
 */
export function TermsPage() {
  const { t } = useTranslation();
  const location = useLocation();
  const session = useSession();
  const hostname = window.location.hostname;
  const from = (location.state as EnrolmentState | null)?.from ?? "/";
  const back = from.startsWith("/terms") ? "/" : from;
  const [checked, setChecked] = useState(false);
  const [leaving, setLeaving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [declining, setDeclining] = useState(false);
  const [problem, setProblem] = useState<ApiError | undefined>();
  const terms = useQuery({ queryKey: myTermsQueryKey, queryFn: ({ signal }) => fetchMyTerms(signal) });

  // /me as it is now: the frame may have come here on a call's refusal, before its own /me knew.
  useEffect(() => {
    void session.refresh().finally(() => setChecked(true));
    // Read once, when the page opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (((checked && !busy) || leaving) && session.me && !session.me.terms_acceptance_required) {
    return <Navigate to={back} replace />;
  }

  const accept = async (documents: Document[]) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await acceptTerms(documents.map(({ source, key, version }) => ({ source, key, version })));
      // The way back waits for /me to show the step gone, as the enrolment's does.
      await session.refresh();
      setLeaving(true);
    } catch (error) {
      setProblem(error instanceof ApiError ? error : undefined);
      // A newer version may have come while the person read: show what is pending now.
      await terms.refetch();
    } finally {
      setBusy(false);
    }
  };

  const decline = async () => {
    setBusy(true);
    try {
      await declineTerms();
    } finally {
      setDeclining(false);
      session.closed("terms-declined");
    }
  };

  const published = (iso: string) => formatDate(iso);
  const pending = terms.data?.pending ?? [];
  return (
    <Container size={760} py="xl" data-terms-required>
      <Stack align="center" gap="xs" mb="md">
        <Welcome decorative size="md" />
        <Group gap="sm" justify="center" wrap="nowrap">
          <TenantMark hostname={hostname} size={32} />
          <Title order={2} variant="display">
            {t("terms.title")}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" ta="center">
          {t("app.tenant", { hostname })}
        </Text>
      </Stack>
      <Stack gap="md">
        <Text size="sm">{t("terms.intro")}</Text>
        {problem && (
          <Alert color="danger" role="alert">
            {problem.message}
            {problem.correlationId !== undefined && (
              <Text size="xs" c="dimmed" mt={4}>
                {t("errors.correlation", { id: problem.correlationId })}
              </Text>
            )}
          </Alert>
        )}
        {!checked || terms.isPending ? (
          <LoadingState height={200} />
        ) : terms.isError ? (
          <Alert color="danger" role="alert" title={t("terms.failed")}>
            {terms.error instanceof ApiError ? terms.error.message : t("errors.unexpected")}
          </Alert>
        ) : (
          pending.map((document) => (
            <Paper
              key={`${document.source}:${document.key}`}
              withBorder
              p="lg"
              component="article"
              aria-labelledby={`terms-${document.key}`}
            >
              <Stack gap="xs">
                <Group justify="space-between" align="flex-start" wrap="wrap">
                  <Title order={3} size="h4" id={`terms-${document.key}`}>
                    {document.title}
                  </Title>
                  <Badge variant="light" color={document.source === "platform" ? "info" : "console"}>
                    {t(`terms.sources.${document.source}`, { defaultValue: document.source })}
                  </Badge>
                </Group>
                <Text size="xs" c="dimmed">
                  {t("terms.version", { version: document.version, date: published(document.published_at) })}
                </Text>
                <ScrollArea.Autosize mah={320} type="auto" offsetScrollbars>
                  <Text
                    size="sm"
                    style={{ whiteSpace: "pre-wrap" }}
                    tabIndex={0}
                    aria-label={t("terms.text", { title: document.title })}
                  >
                    {document.body}
                  </Text>
                </ScrollArea.Autosize>
              </Stack>
            </Paper>
          ))
        )}
        {pending.length > 0 && (
          <Group justify="space-between" wrap="wrap">
            <Button variant="subtle" onClick={() => setDeclining(true)} disabled={busy}>
              {t("terms.decline")}
            </Button>
            <Button onClick={() => void accept(pending)} loading={busy}>
              {pending.length === 1 ? t("terms.acceptOne") : t("terms.acceptAll", { count: pending.length })}
            </Button>
          </Group>
        )}
      </Stack>
      <ConfirmDialog
        opened={declining}
        title={t("terms.declineTitle")}
        message={t("terms.declineMessage")}
        confirmLabel={t("terms.declineConfirm")}
        danger
        busy={busy}
        onConfirm={() => void decline()}
        onCancel={() => setDeclining(false)}
      />
    </Container>
  );
}
