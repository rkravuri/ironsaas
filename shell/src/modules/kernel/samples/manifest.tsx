import { IconFlask } from "@tabler/icons-react";
import { gridShortcuts } from "@/components/shortcuts";
import type { ModuleManifest } from "@/modules/registry";
import { SampleRecordPage } from "./pages/SampleRecordPage";
import { SampleRecordsPage } from "./pages/SampleRecordsPage";

const read = "samples.records.read";

/**
 * Sample records (kernel layer, ADR-0057): the record type of a unit the operator marked for testing, on which the smoke
 * and the browser tests meet the record template with its tags, sharing and history. Its permission exists only in such
 * a unit, so no one elsewhere ever sees its pages.
 */
export const samplesManifest: ModuleManifest = {
  name: "samples",
  titleKey: "modules.samples.title",
  routes: [
    {
      path: "/samples",
      permission: read,
      element: <SampleRecordsPage />,
      titleKey: "modules.samples.records",
      shortcuts: gridShortcuts,
    },
    {
      path: "/samples/:id",
      permission: read,
      element: <SampleRecordPage />,
      titleKey: "modules.samples.records",
    },
  ],
  navigation: [
    {
      labelKey: "modules.samples.records",
      path: "/samples",
      permission: read,
      icon: <IconFlask size={16} />,
    },
  ],
  opener: { accent: "info", lineKey: "modules.samples.line" },
};
