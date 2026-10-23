import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { Button, Group, Text, TextInput } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { complianceQueryKey, fetchEvidencePacks, requestEvidencePack, type EvidencePack } from "@/api/compliance";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { FormDialog } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { formatDateTime } from "@/i18n/dates";
import { today } from "@/modules/kernel/console/pages/delegation-words";
import { packPeriodWords, packStateWords, sizeWords, startOfNextUtcDay, startOfUtcDay } from "./pack-words";

/**
 * Evidence packs (TF-067, ADR-0052) on the list template: every pack made, newest first, and a form to make one for a
 * period. The unit makes it within seconds; its record page says when it is ready and gives the download. Making and
 * downloading are audited with the person's name, and both need compliance.evidence.export, which the route asks for.
 */
export function EvidencePacksPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [requesting, setRequesting] = useState(false);
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchEvidencePacks(query, signal), []);
  const columns: DataGridColumn<EvidencePack>[] = [
    {
      field: "period_from",
      headerKey: "evidencePacks.columns.period",
      flex: 2,
      minWidth: 320,
      format: (pack) => packPeriodWords(pack.period_from, pack.period_to, t),
    },
    {
      field: "status",
      headerKey: "evidencePacks.columns.status",
      width: 160,
      format: (pack) => packStateWords(pack.status, t),
    },
    { field: "requester_name", headerKey: "evidencePacks.columns.requester", flex: 1, minWidth: 170 },
    {
      field: "requested_at",
      headerKey: "evidencePacks.columns.requested",
      width: 215,
      format: (pack) => formatDateTime(pack.requested_at),
    },
    {
      field: "bytes",
      headerKey: "evidencePacks.columns.size",
      width: 110,
      format: (pack) => (pack.bytes === null ? "" : sizeWords(pack.bytes)),
    },
  ];

  return (
    <>
      <ListPage
        title={t("evidencePacks.title")}
        description={t("evidencePacks.description")}
        drawing={
          <Mandala symmetry={12} rings={3} stroke={2} density={0.5} seed="evidence-packs" size={56} accent="success" />
        }
        actions={
          <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setRequesting(true)}>
            {t("evidencePacks.request.open")}
          </Button>
        }
      >
        <DataGrid<EvidencePack>
          columns={columns}
          fetchPage={fetchPage}
          label={t("evidencePacks.gridLabel")}
          onRowOpen={(pack) => void navigate(`/compliance/evidence-packs/${pack.id}`)}
        />
      </ListPage>
      <FormDialog opened={requesting} title={t("evidencePacks.request.title")} onClose={() => setRequesting(false)}>
        {requesting && (
          <RequestPackForm
            onClose={() => setRequesting(false)}
            onMade={(pack) => {
              setRequesting(false);
              void navigate(`/compliance/evidence-packs/${pack.id}`);
            }}
          />
        )}
      </FormDialog>
    </>
  );
}

/** The period of a pack, as calendar days in UTC: from the first day's start up to the end of the last (TF-067 AC1). */
function RequestPackForm({ onClose, onMade }: { onClose: () => void; onMade: (pack: EvidencePack) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const last = today();
  const schema = useMemo(
    () =>
      z
        .object({
          from: z.string().min(1, t("evidencePacks.request.fromRequired")),
          to: z
            .string()
            .min(1, t("evidencePacks.request.toRequired"))
            .refine((value) => value <= last, t("evidencePacks.request.toFuture")),
        })
        .refine((values) => values.to === "" || values.from === "" || values.to >= values.from, {
          path: ["to"],
          message: t("evidencePacks.request.toBeforeFrom"),
        }),
    [last, t],
  );
  const form = useZodForm({ schema, initialValues: { from: "", to: "" } });
  return (
    <Form
      form={form}
      submitLabel={t("evidencePacks.request.confirm")}
      secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      onSubmit={async (values) => {
        const pack = await requestEvidencePack(startOfUtcDay(values.from), startOfNextUtcDay(values.to));
        await queryClient.invalidateQueries({ queryKey: complianceQueryKey });
        onMade(pack);
      }}
    >
      <Text size="sm">{t("evidencePacks.request.intro")}</Text>
      <Group grow align="flex-start">
        <TextInput
          type="date"
          label={t("evidencePacks.request.from")}
          max={last}
          data-autofocus
          {...form.getInputProps("from")}
        />
        <TextInput type="date" label={t("evidencePacks.request.to")} max={last} {...form.getInputProps("to")} />
      </Group>
      <Text size="xs" c="dimmed">
        {t("evidencePacks.request.audited")}
      </Text>
    </Form>
  );
}
