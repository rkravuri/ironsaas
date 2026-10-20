import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { SimpleGrid, Stack, Table, Text, Title } from "@mantine/core";
import { contrastRatio } from "@/design/contrast";
import { accentNames, contrastPairs, textMinimum, tokens, type Scheme } from "@/design/tokens";
import { resolveCssVariables } from "@/theme";

const schemes: Scheme[] = ["light", "dark"];

interface ScaleProps {
  name: string;
  meaningKey: string;
  shades: readonly string[];
}

function Scale({ name, meaningKey, shades }: ScaleProps) {
  const { t } = useTranslation();
  return (
    <Stack gap="xs">
      <Title order={4}>{name}</Title>
      <Text size="sm" c="dimmed">
        {t(meaningKey)}
      </Text>
      <SimpleGrid cols={{ base: 5, sm: 10 }} spacing="xs">
        {shades.map((shade, index) => (
          <Stack key={shade} gap={4} align="center">
            <div
              aria-hidden="true"
              style={{ background: shade, width: "100%", height: 40, borderRadius: "var(--mantine-radius-sm)" }}
            />
            <Text size="xs">{index}</Text>
            <Text size="xs" ff="monospace">
              {shade}
            </Text>
          </Stack>
        ))}
      </SimpleGrid>
    </Stack>
  );
}

/** The scales with their meanings, then every contrast pair measured on the values the theme really emits. */
export function ColourSection() {
  const { t } = useTranslation();
  const rows = useMemo(() => {
    const resolved = resolveCssVariables();
    return schemes.flatMap((scheme) =>
      contrastPairs()
        .filter((pair) => pair.scheme === undefined || pair.scheme === scheme)
        .map((pair) => {
          const foreground = resolved[scheme][pair.foreground] ?? "";
          const background = resolved[scheme][pair.background] ?? "";
          const ratio = foreground !== "" && background !== "" ? contrastRatio(foreground, background) : 0;
          return {
            key: `${scheme}:${pair.foreground}:${pair.background}`,
            scheme,
            pair,
            foreground,
            background,
            ratio,
            pass: ratio >= pair.minimum,
          };
        }),
    );
  }, []);

  return (
    <section aria-labelledby="gallery-colour">
      <Stack gap="lg">
        <Title order={2} id="gallery-colour">
          {t("gallery.sections.colour")}
        </Title>
        <Title order={3}>{t("gallery.colour.scales")}</Title>
        <Text>{t("gallery.colour.scalesIntro")}</Text>
        {accentNames.map((name) => (
          <Scale
            key={name}
            name={name}
            meaningKey={tokens.accents[name].meaningKey}
            shades={tokens.accents[name].shades}
          />
        ))}
        <Scale name="gray" meaningKey="design.neutral.gray" shades={tokens.neutral.gray} />
        <Scale name="dark" meaningKey="design.neutral.dark" shades={tokens.neutral.dark} />
        <Title order={3}>{t("gallery.colour.pairs")}</Title>
        <Text>{t("gallery.colour.pairsIntro")}</Text>
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("gallery.colour.foreground")}</Table.Th>
              <Table.Th>{t("gallery.colour.background")}</Table.Th>
              <Table.Th>{t("gallery.colour.scheme")}</Table.Th>
              <Table.Th>{t("gallery.colour.sample")}</Table.Th>
              <Table.Th>{t("gallery.colour.ratio")}</Table.Th>
              <Table.Th>{t("gallery.colour.minimum")}</Table.Th>
              <Table.Th>{t("gallery.colour.result")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row) => (
              <Table.Tr key={row.key}>
                <Table.Td>
                  <Text size="xs" ff="monospace">
                    {row.pair.foreground}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Text size="xs" ff="monospace">
                    {row.pair.background}
                  </Text>
                </Table.Td>
                <Table.Td>{t(`gallery.scheme.${row.scheme}`)}</Table.Td>
                <Table.Td>
                  {row.pair.minimum >= textMinimum ? (
                    <span
                      style={{ color: row.foreground, background: row.background, padding: "2px 8px", borderRadius: 4 }}
                    >
                      {t("gallery.colour.sampleText")}
                    </span>
                  ) : (
                    <span
                      role="img"
                      aria-label={t("gallery.colour.swatch")}
                      style={{
                        display: "inline-block",
                        width: 32,
                        height: 20,
                        background: row.background,
                        border: `2px solid ${row.foreground}`,
                        borderRadius: 4,
                      }}
                    />
                  )}
                </Table.Td>
                <Table.Td>{row.ratio.toFixed(2)}:1</Table.Td>
                <Table.Td>{row.pair.minimum}:1</Table.Td>
                <Table.Td>{t(row.pass ? "gallery.colour.pass" : "gallery.colour.fail")}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </section>
  );
}
