import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Button, Checkbox, TextInput } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { fetchReviews, fetchUnits, startReview, type Review, type ReviewSummary } from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { today } from "./delegation-words";
import { dueLabel, progressLabel } from "./review-words";
import { UnitPicker } from "./UnitPicker";

const manage = "authz.reviews.manage";
const defaultSort = [{ field: "created_at", direction: "desc" as const }];

/**
 * Access reviews (TF-038) on the list template: the campaigns the reader manages or reviews, newest first, each with
 * its progress over what the reader may see. Starting one needs authz.reviews.manage for the whole tenant.
 */
export function ReviewsPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const canStart = session.tenantPermissions.includes(manage);
  const [starting, setStarting] = useState(false);
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchReviews(query, signal), []);
  const columns: DataGridColumn<ReviewSummary>[] = [
    { field: "name", headerKey: "reviews.columns.name", sortable: true, filter: "text", flex: 3 },
    {
      field: "due_on",
      headerKey: "reviews.columns.due",
      sortable: true,
      width: 160,
      format: (row) => dueLabel(row.due_on, i18n.language),
    },
    {
      field: "status",
      headerKey: "reviews.columns.status",
      width: 140,
      format: (row) => (row.overdue ? t("reviews.status.overdue") : t(`reviews.status.${row.status}`)),
    },
    {
      field: "progress",
      headerKey: "reviews.columns.progress",
      flex: 2,
      format: (row) => progressLabel(row.progress, t),
    },
    {
      field: "created_at",
      headerKey: "reviews.columns.started",
      sortable: true,
      width: 170,
      hidden: true,
      format: (row) => new Date(row.started_at).toLocaleDateString(i18n.language),
    },
  ];

  return (
    <>
      <ListPage
        title={t("reviews.title")}
        description={t("reviews.description")}
        drawing={<Mandala symmetry={8} rings={2} stroke={2} density={0.5} seed="reviews" size={56} accent="console" />}
        actions={
          canStart && (
            <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setStarting(true)}>
              {t("reviews.start.open")}
            </Button>
          )
        }
      >
        <DataGrid<ReviewSummary>
          columns={columns}
          fetchPage={fetchPage}
          defaultSort={defaultSort}
          label={t("reviews.title")}
          onRowOpen={(row) => void navigate(`/console/access-reviews/${row.id}`)}
        />
      </ListPage>
      <FormDialog opened={starting} title={t("reviews.start.title")} onClose={() => setStarting(false)}>
        {starting && (
          <StartReviewForm
            onClose={() => setStarting(false)}
            onDone={async (review) => {
              setStarting(false);
              await navigate(`/console/access-reviews/${review.id}`);
            }}
          />
        )}
      </FormDialog>
    </>
  );
}

/** A campaign over one unit, with or without the units below it, and a due day: its items are taken when it starts. */
function StartReviewForm({ onClose, onDone }: { onClose: () => void; onDone: (review: Review) => Promise<void> }) {
  const { t } = useTranslation();
  const units = useQuery({ queryKey: ["authz", "units"], queryFn: ({ signal }) => fetchUnits(signal) });
  const first = today();
  const schema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("reviews.start.nameRequired")).max(200, t("reviews.start.nameTooLong")),
        due_on: z
          .string()
          .min(1, t("reviews.start.dueRequired"))
          .refine((value) => value >= first, t("reviews.start.duePassed")),
        unit_id: z.string().min(1, t("reviews.start.unitRequired")),
        include_children: z.boolean(),
      }),
    [first, t],
  );
  const form = useZodForm({ schema, initialValues: { name: "", due_on: "", unit_id: "", include_children: true } });
  return (
    <Form
      form={form}
      onSubmit={async (values) =>
        onDone(
          await startReview({
            name: values.name.trim(),
            due_on: values.due_on,
            units: [{ unit_id: values.unit_id, include_children: values.include_children }],
          }),
        )
      }
      submitLabel={t("reviews.start.submit")}
      secondary={{ label: t("dialog.cancel"), onClick: onClose }}
    >
      <TextInput
        label={t("reviews.start.name")}
        description={t("reviews.start.nameHint")}
        data-autofocus
        {...form.getInputProps("name")}
      />
      <TextInput
        type="date"
        label={t("reviews.start.due")}
        description={t("reviews.start.dueHint")}
        min={first}
        {...form.getInputProps("due_on")}
      />
      {units.isPending ? (
        <LoadingState height={120} />
      ) : (
        <UnitPicker
          label={t("reviews.start.unit")}
          units={units.data?.data ?? []}
          value={form.values.unit_id}
          onChange={(id) => form.setFieldValue("unit_id", id)}
          error={typeof form.errors.unit_id === "string" ? form.errors.unit_id : undefined}
          disabled={(unit) => unit.status !== "active"}
        />
      )}
      <Checkbox
        label={t("reviews.start.includeChildren")}
        description={t("reviews.start.includeChildrenHint")}
        {...form.getInputProps("include_children", { type: "checkbox" })}
      />
    </Form>
  );
}
