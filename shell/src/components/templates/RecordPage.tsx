import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Stack, Tabs } from "@mantine/core";
import { RecordHeader, type RecordHeaderProps } from "../panels/RecordHeader";
import { RecordHistory } from "../panels/RecordHistory";

export interface RecordTab {
  value: string;
  label: string;
  panel: ReactNode;
}

export interface RecordPageProps {
  header: RecordHeaderProps;
  tabs: RecordTab[];
  defaultTab?: string;
  /**
   * The record as the audit trail names it: its History tab shows its events (TF-064 AC1, ADR-0050). A page gives it
   * to a person who may read the audit trail; without it there is no History tab.
   */
  history?: { recordType: string; recordId: string } | undefined;
}

/** A record: the header with its status and actions, then the panels behind tabs, the History last (TF-171). */
export function RecordPage({ header, tabs, defaultTab, history }: RecordPageProps) {
  const { t } = useTranslation();
  const all =
    history === undefined
      ? tabs
      : [
          ...tabs,
          {
            value: "history",
            label: t("history.tab"),
            panel: <RecordHistory recordType={history.recordType} recordId={history.recordId} />,
          },
        ];
  const initial = defaultTab ?? all[0]?.value ?? null;
  return (
    <Stack gap="md" data-template="record">
      <RecordHeader {...header} />
      <Tabs defaultValue={initial} keepMounted={false}>
        <Tabs.List>
          {all.map((tab) => (
            <Tabs.Tab key={tab.value} value={tab.value}>
              {tab.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
        {all.map((tab) => (
          <Tabs.Panel key={tab.value} value={tab.value} pt="md">
            {tab.panel}
          </Tabs.Panel>
        ))}
      </Tabs>
    </Stack>
  );
}
