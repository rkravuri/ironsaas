import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Stack, Text, Title } from "@mantine/core";
import { usePrefersReducedMotion } from "@/design/motion";
import { tokens } from "@/design/tokens";

/** Spacing bars, radius boxes, and one box that moves with the motion tokens, or cuts when motion is reduced. */
export function SpaceAndMotionSection() {
  const { t } = useTranslation();
  const reduced = usePrefersReducedMotion();
  const [moved, setMoved] = useState(false);

  return (
    <section aria-labelledby="gallery-space">
      <Stack gap="lg">
        <Title order={2} id="gallery-space">
          {t("gallery.sections.space")}
        </Title>
        <Title order={3}>{t("gallery.space.spacing")}</Title>
        <Stack gap="xs">
          {Object.entries(tokens.spacing).map(([key, value]) => (
            <Group key={key} gap="md" wrap="nowrap">
              <Text size="xs" c="dimmed" w={120} ff="monospace">
                {key}: {value}px
              </Text>
              <div
                aria-hidden="true"
                style={{ width: value, height: 12, background: "var(--mantine-primary-color-filled)", borderRadius: 2 }}
              />
            </Group>
          ))}
        </Stack>
        <Title order={3}>{t("gallery.space.radius")}</Title>
        <Group gap="md">
          {Object.entries(tokens.radius).map(([key, value]) => (
            <Stack key={key} gap={4} align="center">
              <div
                aria-hidden="true"
                style={{
                  width: 64,
                  height: 64,
                  borderRadius: value,
                  border: "2px solid var(--mantine-color-default-border)",
                  background: "var(--mantine-color-default)",
                }}
              />
              <Text size="xs" ff="monospace">
                {key}: {value}px
              </Text>
            </Stack>
          ))}
        </Group>
        <Title order={3}>{t("gallery.space.motion")}</Title>
        <Text size="sm">
          {t("gallery.space.durations", { quick: tokens.motion.duration.quick, calm: tokens.motion.duration.calm })}
        </Text>
        <Group>
          <Button variant="default" onClick={() => setMoved((current) => !current)}>
            {t(moved ? "gallery.space.moveBack" : "gallery.space.move")}
          </Button>
          <div
            aria-hidden="true"
            style={{
              width: 40,
              height: 40,
              borderRadius: "var(--mantine-radius-md)",
              background: "var(--mantine-primary-color-filled)",
              transform: moved ? "translateX(160px)" : "none",
              transition: "transform var(--iron-duration-calm) var(--iron-ease-enter)",
            }}
          />
        </Group>
        <Text size="sm" c="dimmed">
          {t("gallery.space.reduced", { state: t(reduced ? "gallery.space.on" : "gallery.space.off") })}
        </Text>
      </Stack>
    </section>
  );
}
