import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Stack, Text, TextInput, Title } from "@mantine/core";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ConsolePage, DashboardPage, ListPage, WizardPage } from "@/components/templates";
import { RecordSample } from "./RecordSample";
import { pageOf, sampleRows, type SampleRow } from "./sample-rows";

const columns: DataGridColumn<SampleRow>[] = [
  { field: "name", headerKey: "gallery.grid.name", sortable: true, filter: "text", flex: 2 },
  { field: "kind", headerKey: "gallery.grid.kind", filter: "text", width: 140 },
  { field: "status", headerKey: "gallery.grid.status", filter: "text", width: 120 },
];

function Example({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Stack gap="xs">
      <Title order={3}>{title}</Title>
      {children}
    </Stack>
  );
}

/** The five templates on sample content: the opener is the moment, the body is the work. */
export function TemplatesSection() {
  const { t } = useTranslation();
  const rows = useMemo(() => sampleRows(t), [t]);
  const fetchPage = useCallback((query: string) => pageOf(rows, query), [rows]);
  const [step, setStep] = useState(0);
  const steps = [t("gallery.wizard.steps.name"), t("gallery.wizard.steps.parent"), t("gallery.wizard.steps.confirm")];

  return (
    <section aria-labelledby="gallery-templates">
      <Stack gap="xl">
        <Title order={2} id="gallery-templates">
          {t("gallery.sections.templates")}
        </Title>
        <Example title={t("gallery.templates.list")}>
          <div style={{ height: 420 }}>
            <ListPage title={t("gallery.grid.label")} description={t("gallery.templates.listDescription")} actions={<Button>{t("gallery.templates.newRecord")}</Button>}>
              <DataGrid<SampleRow> columns={columns} fetchPage={fetchPage} label={t("gallery.templates.listGrid")} />
            </ListPage>
          </div>
        </Example>
        <Example title={t("gallery.templates.record")}>
          <RecordSample />
        </Example>
        <Example title={t("gallery.templates.wizard")}>
          <WizardPage
            title={t("gallery.wizard.title")}
            description={t("gallery.wizard.description")}
            steps={steps.map((label) => ({ label }))}
            active={step}
            onBack={() => setStep((current) => Math.max(0, current - 1))}
            onNext={() => setStep((current) => Math.min(steps.length - 1, current + 1))}
            summary={<Text size="sm">{t("gallery.wizard.summary", { step: step + 1 })}</Text>}
          >
            <TextInput label={steps[step] ?? ""} />
          </WizardPage>
        </Example>
        <Example title={t("gallery.templates.console")}>
          <ConsolePage
            title={t("gallery.console.title")}
            description={t("gallery.console.description")}
            sections={[
              {
                id: "gallery-console-general",
                title: t("gallery.console.general"),
                description: t("gallery.console.generalDescription"),
                children: <TextInput label={t("gallery.console.tenantName")} defaultValue="Acme" />,
              },
              {
                id: "gallery-console-mail",
                title: t("gallery.console.mail"),
                description: t("gallery.console.mailDescription"),
                children: <TextInput label={t("gallery.console.sender")} defaultValue="noreply@acme.localhost" />,
              },
            ]}
          />
        </Example>
        <Example title={t("gallery.templates.dashboard")}>
          <DashboardPage
            title={t("gallery.dashboard.title")}
            description={t("gallery.dashboard.description")}
            cards={[
              { id: "open", label: t("gallery.dashboard.open"), value: "128", hint: t("gallery.dashboard.openHint") },
              { id: "late", label: t("gallery.dashboard.late"), value: "3", tone: "danger", hint: t("gallery.dashboard.lateHint") },
              { id: "done", label: t("gallery.dashboard.done"), value: "412", tone: "success" },
              { id: "value", label: t("gallery.dashboard.value"), value: "1.2 M" },
            ]}
          />
        </Example>
      </Stack>
    </section>
  );
}
