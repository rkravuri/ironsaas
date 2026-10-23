import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Stack, Text } from "@mantine/core";
import { IconDownload } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { complianceQueryKey, fetchValidation, validationPackPath } from "@/api/compliance";
import { LoadingState } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { sizeWords } from "./pack-words";

/**
 * Validation (TF-218 AC3, ADR-0052) on the console template: the version this organisation's unit runs, and the
 * validation pack of exactly that version, which the fleet placed in the unit's storage at its update. The download is
 * the browser's, with the session; the API checks compliance.validation.read and audits it. A laptop's development
 * build says so: its pack is the last one made on that machine, for trying the page.
 */
export function ValidationPage() {
  const { t } = useTranslation();
  const validation = useQuery({
    queryKey: [...complianceQueryKey, "validation"],
    queryFn: ({ signal }) => fetchValidation(signal),
  });
  if (validation.isPending) {
    return <LoadingState height={240} />;
  }
  if (validation.isError) {
    return (
      <Alert color="danger" role="alert" title={t("validation.failed")}>
        {validation.error instanceof ApiError ? validation.error.message : t("errors.unexpected")}
        {validation.error instanceof ApiError && validation.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: validation.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  const data = validation.data;
  return (
    <ConsolePage
      title={t("validation.title")}
      description={t("validation.description")}
      sections={[
        {
          id: "version",
          title: t("validation.version"),
          children: (
            <Stack gap="xs">
              <Text ff="monospace" size="lg" data-version={data.version}>
                {data.version}
              </Text>
              {data.development && (
                <Alert color="warning" role="status">
                  {t("validation.development")}
                </Alert>
              )}
            </Stack>
          ),
        },
        {
          id: "pack",
          title: t("validation.pack"),
          children: data.pack_available ? (
            <Stack gap="xs" align="flex-start">
              <Text size="sm">
                {t("validation.packReady", {
                  version: data.version,
                  size: data.pack_bytes === null ? "" : sizeWords(data.pack_bytes),
                })}
              </Text>
              <Button
                component="a"
                href={validationPackPath}
                download
                leftSection={<IconDownload size={16} aria-hidden="true" />}
              >
                {t("validation.download")}
              </Button>
              <Text size="xs" c="dimmed">
                {t("validation.audited")}
              </Text>
            </Stack>
          ) : (
            <Alert color="warning" role="status" data-pack-missing>
              {t("validation.packMissing")}
            </Alert>
          ),
        },
      ]}
    />
  );
}
