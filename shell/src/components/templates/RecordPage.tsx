import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Stack, Tabs } from "@mantine/core";
import { IconPrinter } from "@tabler/icons-react";
import { RecordHeader, type RecordHeaderProps } from "../panels/RecordHeader";
import { RecordHistory } from "../panels/RecordHistory";
import { RecordSignatures } from "../panels/SignatureBlock";

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
  /**
   * The record as its signatures name it (TF-219 AC1, ADR-0051): the signature block shows under the header, on screen
   * and in print, and the page offers to print. Without it the record has no block.
   */
  signatures?: { recordType: string; recordId: string } | undefined;
}

/**
 * A record: the header with its status and actions, its signatures when it has any, then the panels behind tabs, the
 * History last (TF-171). Printed, the frame and the tab list are left out (the global print style): the header, the
 * signature block and the open tab are the record on paper.
 */
export function RecordPage({ header, tabs, defaultTab, history, signatures }: RecordPageProps) {
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
      {signatures !== undefined && (
        <>
          <Group justify="flex-end" data-print="hide">
            <Button
              variant="default"
              size="xs"
              leftSection={<IconPrinter size={14} aria-hidden="true" />}
              onClick={() => window.print()}
            >
              {t("signatures.print")}
            </Button>
          </Group>
          <RecordSignatures recordType={signatures.recordType} recordId={signatures.recordId} />
        </>
      )}
      <Tabs defaultValue={initial} keepMounted={false}>
        <Tabs.List data-print="hide">
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
