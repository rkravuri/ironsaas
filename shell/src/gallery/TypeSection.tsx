import { useTranslation } from "react-i18next";
import { Group, Stack, Text, Title } from "@mantine/core";
import { tokens, type TypeSize } from "@/design/tokens";

const levels = [1, 2, 3, 4, 5, 6] as const;

/** The six sizes, the heading levels, the display family where it is allowed, and tabular figures. */
export function TypeSection() {
  const { t } = useTranslation();
  const sizes = Object.entries(tokens.type.sizes) as [TypeSize, { px: number; lineHeight: number }][];

  return (
    <section aria-labelledby="gallery-type">
      <Stack gap="lg">
        <Title order={2} id="gallery-type">
          {t("gallery.sections.type")}
        </Title>
        <Title order={3}>{t("gallery.type.sizes")}</Title>
        <Stack gap="sm">
          {sizes.map(([key, size]) => (
            <Group key={key} align="baseline" gap="md" wrap="nowrap">
              <Text size="xs" c="dimmed" w={120} ff="monospace">
                {key}: {size.px}/{size.lineHeight}
              </Text>
              <Text fz={key} lh={String(size.lineHeight / size.px)}>
                {t("gallery.type.sample")}
              </Text>
            </Group>
          ))}
        </Stack>
        <Title order={3}>{t("gallery.type.headings")}</Title>
        <Stack gap="xs">
          {levels.map((order) => (
            <Title key={order} order={order}>
              {t("gallery.type.heading", { order })}
            </Title>
          ))}
        </Stack>
        <Title order={3}>{t("gallery.type.display")}</Title>
        <Title order={1} variant="display">
          {t("gallery.type.displaySample")}
        </Title>
        <Title order={2} variant="display">
          {t("gallery.type.displaySample")}
        </Title>
        <Title order={3}>{t("gallery.type.tabular")}</Title>
        <Text style={{ fontVariantNumeric: "tabular-nums" }}>{t("gallery.type.tabularSample")}</Text>
      </Stack>
    </section>
  );
}
