import { useTranslation } from "react-i18next";
import { Group, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import { illustrationNames, illustrations } from "@/design/illustrations";
import { Hatch, Mandala, ModuleMark, symmetries, TenantMark } from "@/design/patterns";
import type { AccentName } from "@/design/tokens";

const tenants = ["acme.localhost", "bravo.localhost"];
/** The modules of the shell today, by name and accent, as their manifests declare them. */
const modules: { name: string; accent: AccentName; titleKey: string }[] = [
  { name: "identity", accent: "identity", titleKey: "modules.identity.title" },
  { name: "console", accent: "console", titleKey: "modules.console.title" },
];

/** The patterns and the drawings: two tenant marks side by side, the generator's symmetries, the hatch, every drawing with its name. */
export function DrawingsSection() {
  const { t } = useTranslation();

  return (
    <section aria-labelledby="gallery-drawings">
      <Stack gap="lg">
        <Title order={2} id="gallery-drawings">
          {t("gallery.sections.drawings")}
        </Title>
        <Title order={3}>{t("gallery.drawings.marks")}</Title>
        <Text>{t("gallery.drawings.marksIntro")}</Text>
        <Group gap="xl" align="flex-start">
          {tenants.map((hostname) => (
            <Stack key={hostname} gap="xs" align="center">
              <TenantMark hostname={hostname} size={160} animate />
              <Text size="sm" ff="monospace">
                {hostname}
              </Text>
            </Stack>
          ))}
        </Group>
        <Title order={3}>{t("gallery.drawings.moduleMarks")}</Title>
        <Text>{t("gallery.drawings.moduleMarksIntro")}</Text>
        <Group gap="xl" align="flex-start">
          {modules.map((module) => (
            <Stack key={module.name} gap="xs" align="center">
              <ModuleMark name={module.name} accent={module.accent} size={96} animate />
              <Text size="sm">{t(module.titleKey)}</Text>
            </Stack>
          ))}
        </Group>
        <Title order={3}>{t("gallery.drawings.patterns")}</Title>
        <Text>{t("gallery.drawings.patternsIntro")}</Text>
        <Group gap="xl" align="flex-start">
          {symmetries.map((symmetry) => (
            <Stack key={symmetry} gap="xs" align="center">
              <Mandala symmetry={symmetry} rings={4} stroke={2} density={0.7} seed={`gallery-${symmetry}`} size={120} />
              <Text size="sm">{t("gallery.drawings.symmetry", { count: symmetry })}</Text>
            </Stack>
          ))}
          <Stack gap="xs" align="center">
            <svg aria-hidden="true" focusable="false" width={120} height={120} viewBox="0 0 120 120">
              <defs>
                <Hatch id="gallery-hatch" />
              </defs>
              <rect
                x={10}
                y={10}
                width={100}
                height={100}
                rx={8}
                fill="url(#gallery-hatch)"
                stroke="var(--iron-ink)"
                strokeWidth={2}
              />
            </svg>
            <Text size="sm">{t("gallery.drawings.hatch")}</Text>
          </Stack>
        </Group>
        <Title order={3}>{t("gallery.drawings.illustrations")}</Title>
        <Text>{t("gallery.drawings.illustrationsIntro")}</Text>
        <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="lg">
          {illustrationNames.map((name) => {
            const Illustration = illustrations[name];
            return (
              <Stack key={name} gap="xs" align="center">
                <Illustration size="lg" />
                <Text size="sm" ta="center">
                  {t(`illustrations.${name}`)}
                </Text>
              </Stack>
            );
          })}
        </SimpleGrid>
      </Stack>
    </section>
  );
}
