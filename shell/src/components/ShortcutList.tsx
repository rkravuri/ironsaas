import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { Group, Kbd, Table, Text } from "@mantine/core";
import { isChord, keyName, onMac, type Shortcut } from "./shortcuts";

/** Keys and what they do, as a table: the keys in `Kbd`, a chord joined by "+", alternatives by "/". */
export function ShortcutList({ shortcuts, label }: { shortcuts: readonly Shortcut[]; label: string }) {
  const { t } = useTranslation();
  const mac = onMac();
  const name = (key: string) => keyName(key, mac, (k) => t(k, { defaultValue: "" }));
  return (
    <Table aria-label={label} verticalSpacing={6}>
      <Table.Tbody>
        {shortcuts.map((shortcut) => (
          <Table.Tr key={shortcut.labelKey}>
            <Table.Td w="40%">
              <Group gap={4} wrap="nowrap">
                {shortcut.keys.map((key, index) => (
                  <Fragment key={key}>
                    {index > 0 && (
                      <Text span size="xs" c="dimmed" aria-hidden="true">
                        {isChord(shortcut) ? "+" : "/"}
                      </Text>
                    )}
                    <Kbd size="sm">{name(key)}</Kbd>
                  </Fragment>
                ))}
              </Group>
            </Table.Td>
            <Table.Td>
              <Text size="sm">{t(shortcut.labelKey)}</Text>
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}
