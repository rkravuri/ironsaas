import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { Group, SegmentedControl } from "@mantine/core";
import type { GalleryState } from "./gallery-state";

/** Two switches that write the URL; the page reads it back, so the state has one source. */
export function GalleryControls({ scheme, density }: GalleryState) {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    next.set(key, value);
    setParams(next, { replace: true });
  };

  return (
    <Group gap="lg">
      <SegmentedControl
        aria-label={t("gallery.scheme.label")}
        value={scheme}
        onChange={(value) => update("scheme", value)}
        data={[
          { value: "light", label: t("gallery.scheme.light") },
          { value: "dark", label: t("gallery.scheme.dark") },
        ]}
      />
      <SegmentedControl
        aria-label={t("gallery.density.label")}
        value={density}
        onChange={(value) => update("density", value)}
        data={[
          { value: "comfortable", label: t("gallery.density.comfortable") },
          { value: "compact", label: t("gallery.density.compact") },
        ]}
      />
    </Group>
  );
}
