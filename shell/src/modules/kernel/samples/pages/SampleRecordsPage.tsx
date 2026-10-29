import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { Button, TextInput } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { createSampleRecord, fetchSampleRecords, type SampleRecord } from "@/api/samples";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { FormDialog } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";

const manage = "samples.records.manage";
const byName = [{ field: "name", direction: "asc" as const }];

/**
 * Sample records (ADR-0057), only in a unit the operator marked for testing: the records the reader may see, through
 * their policy, so a reader who holds visibility tags sees only the untagged ones and those that share a tag with them.
 */
export function SampleRecordsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const manages = useSession().permissions.includes(manage);
  const [creating, setCreating] = useState(false);
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchSampleRecords(query, signal), []);
  const columns = useMemo<DataGridColumn<SampleRecord>[]>(
    () => [
      { field: "name", headerKey: "samples.columns.name", sortable: true, filter: "text", flex: 2 },
      {
        field: "visibility_tags",
        headerKey: "samples.columns.tags",
        flex: 1,
        format: (row) => (row.visibility_tags.length === 0 ? t("samples.noTags") : row.visibility_tags.join(", ")),
      },
      {
        field: "updated_at",
        headerKey: "samples.columns.updated",
        width: 200,
        sortable: true,
        format: (row) => formatDateTime(row.updated_at),
      },
    ],
    [t],
  );
  return (
    <ListPage
      title={t("samples.title")}
      description={t("samples.description")}
      actions={
        manages && (
          <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setCreating(true)}>
            {t("samples.new")}
          </Button>
        )
      }
    >
      <DataGrid<SampleRecord>
        columns={columns}
        fetchPage={fetchPage}
        defaultSort={byName}
        label={t("samples.gridLabel")}
        onRowOpen={(row) => void navigate(`/samples/${row.id}`)}
      />
      {creating && (
        <NewSampleDialog
          onClose={() => setCreating(false)}
          onCreated={(record) => void navigate(`/samples/${record.id}`)}
        />
      )}
    </ListPage>
  );
}

interface NewValues extends Record<string, unknown> {
  name: string;
}

/** A new sample record at the person's working unit: its name. */
function NewSampleDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (record: SampleRecord) => void }) {
  const { t } = useTranslation();
  const schema = useMemo(() => z.object({ name: z.string().trim().min(1, t("samples.nameRequired")).max(200) }), [t]);
  const form = useZodForm<NewValues>({ schema, initialValues: { name: "" } });
  return (
    <FormDialog opened title={t("samples.newTitle")} onClose={onClose}>
      <Form
        form={form}
        onSubmit={async (values) => onCreated(await createSampleRecord(values.name.trim()))}
        submitLabel={t("samples.create")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <TextInput label={t("samples.name")} required data-autofocus {...form.getInputProps("name")} />
      </Form>
    </FormDialog>
  );
}
