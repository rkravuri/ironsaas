import { IconInfoCircle } from "@tabler/icons-react";
import type { ModuleManifest } from "@/modules/registry";
import { AboutSuitePage } from "./pages/AboutSuitePage";

const read = "purchasing.suite.read";

/**
 * Purchasing, the eProcurement Suite's shared master (ADR-0043, ADR-0058). Today it holds the suite's About page, which
 * shows an administrator what of the suite the tenant has and under which plan (TF-244, ADR-0059); purchasing groups
 * and profiles join it in PR0. Licensed with the suite: a tenant without the suite never sees it.
 */
export const purchasingManifest: ModuleManifest = {
  name: "purchasing",
  licence: "purchasing",
  suite: "procurement",
  titleKey: "modules.purchasing.title",
  routes: [
    {
      path: "/procurement/about",
      permission: read,
      element: <AboutSuitePage />,
      titleKey: "modules.purchasing.about",
    },
  ],
  navigation: [
    {
      labelKey: "modules.purchasing.about",
      path: "/procurement/about",
      permission: read,
      icon: <IconInfoCircle size={16} />,
    },
  ],
  opener: { accent: "console", lineKey: "modules.purchasing.line" },
};
