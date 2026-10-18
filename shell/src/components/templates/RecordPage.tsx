import type { ReactNode } from "react";
import { Stack, Tabs } from "@mantine/core";
import { RecordHeader, type RecordHeaderProps } from "../panels/RecordHeader";

export interface RecordTab {
  value: string;
  label: string;
  panel: ReactNode;
}

export interface RecordPageProps {
  header: RecordHeaderProps;
  tabs: RecordTab[];
  defaultTab?: string;
}

/** A record: the header with its status and actions, then the panels behind tabs (TF-171). */
export function RecordPage({ header, tabs, defaultTab }: RecordPageProps) {
  const initial = defaultTab ?? tabs[0]?.value ?? null;
  return (
    <Stack gap="md" data-template="record">
      <RecordHeader {...header} />
      <Tabs defaultValue={initial} keepMounted={false}>
        <Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.value} value={tab.value}>
              {tab.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
        {tabs.map((tab) => (
          <Tabs.Panel key={tab.value} value={tab.value} pt="md">
            {tab.panel}
          </Tabs.Panel>
        ))}
      </Tabs>
    </Stack>
  );
}
