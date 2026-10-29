import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Box, Button, Group, Select, Stack, Table, Text, TextInput, Textarea } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  changeVisibilityTag,
  createVisibilityTag,
  fetchTagHolders,
  fetchVisibilityTags,
  reactivateVisibilityTag,
  retireVisibilityTag,
  visibilityTagsQueryKey,
  type TagHolder,
  type VisibilityTag,
} from "@/api/visibility";
import { useSession } from "@/auth/session-context";
import { useReadsAuditTrail } from "@/auth/record-history";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, FormDialog, LoadingState, RecordHistoryButton } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDate } from "@/i18n/dates";

const manageTags = "visibility.tags.manage";
const codePattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/;
const holderSort = [{ field: "created_at", direction: "desc" as const }];

interface TagValues extends Record<string, unknown> {
  code: string;
  name: string;
  description: string;
}

/**
 * Content visibility (TF-241, ADR-0057), for the holders of visibility.tags.read: the tenant's visibility tags, defined,
 * renamed, retired and reactivated with visibility.tags.manage, and for each tag who holds it. A person who holds tags
 * sees a tagged record only when they share one of its tags; one who holds none sees every record their roles allow.
 * People get their tags on their user record.
 */
export function VisibilityPage() {
  const { t } = useTranslation();
  const session = useSession();
  const manages = session.permissions.includes(manageTags);
  const readsTrail = useReadsAuditTrail();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<VisibilityTag | "new" | undefined>();
  const [retiring, setRetiring] = useState<VisibilityTag | undefined>();
  const [chosen, setChosen] = useState<string | null>(null);
  const [done, setDone] = useState<string | undefined>();
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [busy, setBusy] = useState(false);
  const tags = useQuery({ queryKey: visibilityTagsQueryKey, queryFn: ({ signal }) => fetchVisibilityTags(signal) });
  const rows = tags.data?.data ?? [];

  const after = async (message: string) => {
    setDone(message);
    await queryClient.invalidateQueries({ queryKey: visibilityTagsQueryKey });
  };

  const run = async (action: () => Promise<string>) => {
    setBusy(true);
    setFailure(undefined);
    setDone(undefined);
    try {
      await after(await action());
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  const fetchHolders = useCallback(
    (query: string, signal?: AbortSignal) => fetchTagHolders(chosen ?? "", query, signal),
    [chosen],
  );
  const holderColumns: DataGridColumn<TagHolder>[] = [
    { field: "name", headerKey: "visibility.holders.columns.name", flex: 2 },
    {
      field: "type",
      headerKey: "visibility.holders.columns.type",
      width: 180,
      format: (row) => t(`users.types.${row.type}`, { defaultValue: row.type }),
    },
    {
      field: "created_at",
      headerKey: "visibility.holders.columns.since",
      width: 180,
      sortable: true,
      format: (row) => formatDate(row.since),
    },
  ];

  return (
    <>
      <ConsolePage
        title={t("visibility.page.title")}
        description={t("visibility.page.description")}
        sections={[
          {
            id: "tags",
            title: t("visibility.tags.title"),
            description: t("visibility.tags.description"),
            children: (
              <Stack gap="sm">
                {manages && (
                  <Group justify="flex-end">
                    <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setEditing("new")}>
                      {t("visibility.tags.new")}
                    </Button>
                  </Group>
                )}
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
                {tags.isPending ? (
                  <LoadingState height={120} />
                ) : tags.isError ? (
                  <Alert color="danger" role="alert">
                    {tags.error instanceof ApiError ? tags.error.message : t("errors.unexpected")}
                  </Alert>
                ) : rows.length === 0 ? (
                  <Text size="sm" c="dimmed">
                    {t("visibility.tags.none")}
                  </Text>
                ) : (
                  <Table withTableBorder striped data-visibility-tags-table>
                    <Table.Caption>{t("visibility.tags.caption")}</Table.Caption>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>{t("visibility.tags.columns.tag")}</Table.Th>
                        <Table.Th>{t("visibility.tags.columns.description")}</Table.Th>
                        <Table.Th>{t("visibility.tags.columns.status")}</Table.Th>
                        <Table.Th ta="right">{t("visibility.tags.columns.holders")}</Table.Th>
                        {(manages || readsTrail) && <Table.Th>{t("visibility.tags.columns.actions")}</Table.Th>}
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {rows.map((row) => (
                        <Table.Tr key={row.id} data-tag-row={row.code}>
                          <Table.Td>
                            <Text size="sm" fw={600}>
                              {row.name}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {row.code}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">{row.description ?? ""}</Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">{t(`visibility.status.${row.status}`)}</Text>
                          </Table.Td>
                          <Table.Td ta="right">{row.holders}</Table.Td>
                          {(manages || readsTrail) && (
                            <Table.Td>
                              <Group gap="xs" wrap="nowrap">
                                {manages && (
                                  <Button
                                    variant="subtle"
                                    size="compact-sm"
                                    onClick={() => setEditing(row)}
                                    aria-label={t("visibility.tags.renameNamed", { name: row.name })}
                                  >
                                    {t("visibility.tags.rename")}
                                  </Button>
                                )}
                                {manages && row.status === "active" && (
                                  <Button
                                    variant="subtle"
                                    color="danger"
                                    size="compact-sm"
                                    onClick={() => setRetiring(row)}
                                    aria-label={t("visibility.tags.retireNamed", { name: row.name })}
                                  >
                                    {t("visibility.tags.retire")}
                                  </Button>
                                )}
                                {manages && row.status === "retired" && (
                                  <Button
                                    variant="subtle"
                                    size="compact-sm"
                                    disabled={busy}
                                    onClick={() =>
                                      void run(async () => {
                                        await reactivateVisibilityTag(row.id);
                                        return t("visibility.tags.reactivated", { name: row.name });
                                      })
                                    }
                                    aria-label={t("visibility.tags.reactivateNamed", { name: row.name })}
                                  >
                                    {t("visibility.tags.reactivate")}
                                  </Button>
                                )}
                                {readsTrail && (
                                  <RecordHistoryButton
                                    compact
                                    record={{ recordType: "visibility_tag", recordId: row.id }}
                                    name={row.name}
                                  />
                                )}
                              </Group>
                            </Table.Td>
                          )}
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                )}
              </Stack>
            ),
          },
          {
            id: "holders",
            title: t("visibility.holders.title"),
            description: t("visibility.holders.description"),
            children: (
              <Stack gap="sm">
                <Select
                  label={t("visibility.holders.tag")}
                  placeholder={t("visibility.holders.choose")}
                  data={rows.map((row) => ({ value: row.id, label: `${row.name} (${row.code})` }))}
                  value={chosen}
                  onChange={setChosen}
                  w={360}
                />
                {chosen === null ? (
                  <Text size="sm" c="dimmed">
                    {t("visibility.holders.chooseFirst")}
                  </Text>
                ) : (
                  <Box h={360}>
                    <DataGrid<TagHolder>
                      key={chosen}
                      columns={holderColumns}
                      fetchPage={fetchHolders}
                      defaultSort={holderSort}
                      label={t("visibility.holders.gridLabel")}
                    />
                  </Box>
                )}
              </Stack>
            ),
          },
        ]}
      />
      <ConfirmDialog
        opened={retiring !== undefined}
        title={t("visibility.tags.retireTitle", { name: retiring?.name ?? "" })}
        message={t("visibility.tags.retireMessage")}
        confirmLabel={t("visibility.tags.retire")}
        danger
        busy={busy}
        onConfirm={() => {
          const tag = retiring;
          setRetiring(undefined);
          if (tag) {
            void run(async () => {
              await retireVisibilityTag(tag.id);
              return t("visibility.tags.retired", { name: tag.name });
            });
          }
        }}
        onCancel={() => setRetiring(undefined)}
      />
      {editing !== undefined && (
        <TagDialog
          tag={editing === "new" ? undefined : editing}
          onClose={() => setEditing(undefined)}
          onSaved={async (message) => {
            setEditing(undefined);
            setFailure(undefined);
            await after(message);
          }}
        />
      )}
    </>
  );
}

/** A new tag (its code, kept for good, a name and a description), or a tag's new name and description. */
function TagDialog({
  tag,
  onClose,
  onSaved,
}: {
  tag: VisibilityTag | undefined;
  onClose: () => void;
  onSaved: (message: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z.object({
        code: z.string().trim().regex(codePattern, t("visibility.dialog.codeRule")),
        name: z.string().trim().min(1, t("visibility.dialog.nameRequired")).max(100),
        description: z.string().trim().max(500),
      }),
    [t],
  );
  const form = useZodForm<TagValues>({
    schema,
    initialValues: { code: tag?.code ?? "", name: tag?.name ?? "", description: tag?.description ?? "" },
  });
  const submit = async (values: TagValues) => {
    const description = values.description.trim() === "" ? null : values.description.trim();
    if (tag === undefined) {
      const created = await createVisibilityTag({ code: values.code.trim(), name: values.name.trim(), description });
      await onSaved(t("visibility.tags.created", { name: created.name, code: created.code }));
    } else {
      const changed = await changeVisibilityTag(tag.id, { name: values.name.trim(), description });
      await onSaved(t("visibility.tags.changed", { name: changed.name }));
    }
  };
  return (
    <FormDialog
      opened
      title={
        tag === undefined ? t("visibility.dialog.newTitle") : t("visibility.dialog.renameTitle", { name: tag.name })
      }
      onClose={onClose}
    >
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={tag === undefined ? t("visibility.dialog.create") : t("visibility.dialog.save")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <TextInput
          label={t("visibility.dialog.code")}
          description={t("visibility.dialog.codeHint")}
          required
          readOnly={tag !== undefined}
          data-autofocus={tag === undefined ? true : undefined}
          {...form.getInputProps("code")}
        />
        <TextInput
          label={t("visibility.dialog.name")}
          required
          data-autofocus={tag === undefined ? undefined : true}
          {...form.getInputProps("name")}
        />
        <Textarea
          label={t("visibility.dialog.description")}
          autosize
          minRows={2}
          maxRows={6}
          {...form.getInputProps("description")}
        />
      </Form>
    </FormDialog>
  );
}
