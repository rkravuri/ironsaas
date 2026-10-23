import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Box, Button, Group, Select, Stack, Table, Text, TextInput, Textarea } from "@mantine/core";
import { IconDownload, IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  acceptancesExportUrl,
  documentsQueryKey,
  fetchAcceptances,
  fetchDocuments,
  publishDocument,
  retireDocument,
  type Acceptance,
  type DocumentSummary,
} from "@/api/identity";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, FormDialog, LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDate, formatDateTime } from "@/i18n/dates";

const acceptedSort = [{ field: "accepted_at", direction: "desc" as const }];
const keyPattern = /^[a-z][a-z0-9-]*$/;

interface PublishValues extends Record<string, unknown> {
  key: string;
  title: string;
  body: string;
}

/**
 * The documents people accept (TF-028, ADR-0049), for the holders of identity.documents.manage: the platform's terms of
 * service and privacy notice, which the operator publishes, and the organisation's own, whose next version is published
 * here; everyone accepts a new version at their next request. Then who accepted which version of which document, and
 * when, with a CSV export of the same.
 */
export function DocumentsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [publishing, setPublishing] = useState<{ key: string; title: string } | undefined>();
  const [document, setDocument] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [done, setDone] = useState<string | undefined>();
  const [retiring, setRetiring] = useState<DocumentSummary | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();

  const retire = async () => {
    if (!retiring) {
      return;
    }
    setBusy(true);
    setFailure(undefined);
    try {
      await retireDocument(retiring.key);
      setDone(t("documents.retired", { title: retiring.title }));
      await queryClient.invalidateQueries({ queryKey: documentsQueryKey });
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setRetiring(undefined);
    }
  };
  const documents = useQuery({ queryKey: documentsQueryKey, queryFn: ({ signal }) => fetchDocuments(signal) });
  const filter = document ? `filter=${encodeURIComponent(`document_key:eq:${document}`)}` : "";
  const fetchPage = useCallback(
    (query: string, signal?: AbortSignal) =>
      fetchAcceptances([query, filter].filter((part) => part !== "").join("&"), signal),
    [filter],
  );
  const published = (iso: string) => formatDate(iso);
  const when = (iso: string) => formatDateTime(iso);
  const rows = documents.data?.documents ?? [];

  const columns: DataGridColumn<Acceptance>[] = [
    { field: "display_name", headerKey: "documents.columns.person", flex: 2, filter: "text" },
    { field: "login_id", headerKey: "documents.columns.login", flex: 2, filter: "text" },
    { field: "document_key", headerKey: "documents.columns.document", flex: 2, sortable: true },
    { field: "version", headerKey: "documents.columns.version", width: 110, sortable: true, filter: "number" },
    {
      field: "accepted_at",
      headerKey: "documents.columns.acceptedAt",
      width: 190,
      sortable: true,
      format: (row) => when(row.accepted_at),
    },
  ];

  return (
    <>
      <ConsolePage
        title={t("documents.title")}
        description={t("documents.description")}
        sections={[
          {
            id: "current",
            title: t("documents.current.title"),
            description: t("documents.current.description"),
            children: (
              <Stack gap="sm">
                <Group justify="flex-end">
                  <Button
                    leftSection={<IconPlus size={16} aria-hidden="true" />}
                    onClick={() => setPublishing({ key: "", title: "" })}
                  >
                    {t("documents.publishNew")}
                  </Button>
                </Group>
                {failure && (
                  <Alert color="danger" role="alert">
                    {failure.message}
                    {failure.correlationId !== undefined && (
                      <Text size="xs" c="dimmed" mt={4}>
                        {t("errors.correlation", { id: failure.correlationId })}
                      </Text>
                    )}
                  </Alert>
                )}
                {done && (
                  <Alert
                    color="success"
                    role="status"
                    withCloseButton
                    closeButtonLabel={t("dialog.close")}
                    onClose={() => setDone(undefined)}
                  >
                    {done}
                  </Alert>
                )}
                {documents.isPending ? (
                  <LoadingState height={120} />
                ) : documents.isError ? (
                  <Alert color="danger" role="alert">
                    {documents.error instanceof ApiError ? documents.error.message : t("errors.unexpected")}
                  </Alert>
                ) : rows.length === 0 ? (
                  <Text size="sm" c="dimmed">
                    {t("documents.current.none")}
                  </Text>
                ) : (
                  <Table withTableBorder striped data-documents>
                    <Table.Caption>{t("documents.current.caption")}</Table.Caption>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("documents.columns.document")}</Table.Th>
                        <Table.Th>{t("documents.columns.source")}</Table.Th>
                        <Table.Th ta="right">{t("documents.columns.version")}</Table.Th>
                        <Table.Th>{t("documents.columns.published")}</Table.Th>
                        <Table.Th ta="right">{t("documents.columns.accepted")}</Table.Th>
                        <Table.Th>{t("documents.columns.actions")}</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rows.map((row) => (
                        <Table.Tr key={`${row.source}:${row.key}`}>
                          <Table.Td>
                            <Text size="sm" fw={600}>
                              {row.title}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {row.key}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Badge variant="light" color={row.source === "platform" ? "info" : "console"}>
                              {t(`terms.sources.${row.source}`, { defaultValue: row.source })}
                            </Badge>
                          </Table.Td>
                          <Table.Td ta="right">{row.version}</Table.Td>
                          <Table.Td>{published(row.published_at)}</Table.Td>
                          <Table.Td ta="right">{row.accepted}</Table.Td>
                          <Table.Td>
                            {row.source === "tenant" ? (
                              <Group gap="xs" wrap="nowrap">
                                <Button
                                  variant="subtle"
                                  size="compact-sm"
                                  onClick={() => setPublishing({ key: row.key, title: row.title })}
                                  aria-label={t("documents.publishNextNamed", { title: row.title })}
                                >
                                  {t("documents.publishNext")}
                                </Button>
                                <Button
                                  variant="subtle"
                                  color="danger"
                                  size="compact-sm"
                                  onClick={() => setRetiring(row)}
                                  aria-label={t("documents.retireNamed", { title: row.title })}
                                >
                                  {t("documents.retire")}
                                </Button>
                              </Group>
                            ) : (
                              <Text size="xs" c="dimmed">
                                {t("documents.byOperator")}
                              </Text>
                            )}
                          </Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                )}
              </Stack>
            ),
          },
          {
            id: "accepted",
            title: t("documents.accepted.title"),
            description: t("documents.accepted.description"),
            children: (
              <Stack gap="sm">
                <Group justify="space-between" align="flex-end" wrap="wrap">
                  <Select
                    label={t("documents.accepted.filter")}
                    placeholder={t("documents.accepted.all")}
                    data={rows.map((row) => ({ value: row.key, label: row.title }))}
                    value={document}
                    onChange={(value) => {
                      setDocument(value);
                      setRefreshKey((key) => key + 1);
                    }}
                    clearable
                    w={320}
                  />
                  <Button
                    component="a"
                    href={acceptancesExportUrl(filter)}
                    download="acceptances.csv"
                    variant="default"
                    leftSection={<IconDownload size={16} aria-hidden="true" />}
                  >
                    {t("documents.accepted.export")}
                  </Button>
                </Group>
                <Box h={420}>
                  <DataGrid<Acceptance>
                    columns={columns}
                    fetchPage={fetchPage}
                    defaultSort={acceptedSort}
                    label={t("documents.accepted.gridLabel")}
                    refreshKey={refreshKey}
                  />
                </Box>
              </Stack>
            ),
          },
        ]}
      />
      <ConfirmDialog
        opened={retiring !== undefined}
        title={t("documents.retireTitle", { title: retiring?.title ?? "" })}
        message={t("documents.retireMessage")}
        confirmLabel={t("documents.retire")}
        danger
        busy={busy}
        onConfirm={() => void retire()}
        onCancel={() => setRetiring(undefined)}
      />
      {publishing && (
        <PublishDialog
          initial={publishing}
          onClose={() => setPublishing(undefined)}
          onPublished={async (title, version) => {
            setPublishing(undefined);
            setDone(t("documents.published", { title, version }));
            setRefreshKey((key) => key + 1);
            await queryClient.invalidateQueries({ queryKey: documentsQueryKey });
          }}
        />
      )}
    </>
  );
}

/** A tenant document's next version, or the first of a new one: its key, title and text. */
function PublishDialog({
  initial,
  onClose,
  onPublished,
}: {
  initial: { key: string; title: string };
  onClose: () => void;
  onPublished: (title: string, version: number) => Promise<void>;
}) {
  const { t } = useTranslation();
  const existing = initial.key !== "";
  const schema = useMemo(
    () =>
      z.object({
        key: z.string().trim().regex(keyPattern, t("documents.publish.keyRule")).max(64),
        title: z.string().trim().min(1, t("documents.publish.titleRequired")).max(200),
        body: z.string().trim().min(1, t("documents.publish.bodyRequired")).max(100_000),
      }),
    [t],
  );
  const form = useZodForm<PublishValues>({
    schema,
    initialValues: { key: initial.key, title: initial.title, body: "" },
  });
  const submit = async (values: PublishValues) => {
    const document = await publishDocument(values.key.trim(), values.title.trim(), values.body);
    await onPublished(document.title, document.version);
  };
  return (
    <FormDialog
      opened
      size="lg"
      title={existing ? t("documents.publish.nextTitle", { title: initial.title }) : t("documents.publish.newTitle")}
      onClose={onClose}
    >
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("documents.publish.confirm")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{t("documents.publish.message")}</Text>
        <TextInput
          label={t("documents.publish.key")}
          description={t("documents.publish.keyHint")}
          required
          readOnly={existing}
          {...form.getInputProps("key")}
        />
        <TextInput
          label={t("documents.publish.titleLabel")}
          required
          data-autofocus={existing ? undefined : true}
          {...form.getInputProps("title")}
        />
        <Textarea
          label={t("documents.publish.body")}
          description={t("documents.publish.bodyHint")}
          required
          autosize
          minRows={8}
          maxRows={18}
          {...form.getInputProps("body")}
        />
      </Form>
    </FormDialog>
  );
}
