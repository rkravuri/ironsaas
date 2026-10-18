import { useTranslation } from "react-i18next";
import { Center, Stack, Text } from "@mantine/core";
import { Loading } from "@/design/illustrations";

/** A wait with a shape: the drawn loader and a word, never a spinner (brief section 7). */
export function LoadingState({ text, height = 200 }: { text?: string; height?: string | number }) {
  const { t } = useTranslation();
  return (
    <Center h={height} data-panel="loading-state">
      <Stack align="center" gap="xs">
        <Loading decorative size="sm" />
        <Text c="dimmed" size="sm">
          {text ?? t("app.loading")}
        </Text>
      </Stack>
    </Center>
  );
}
