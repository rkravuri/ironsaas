import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Anchor, Group, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconSignature } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { complianceQueryKey, fetchSignatures, type Signature } from "@/api/compliance";
import { formatDateTime } from "@/i18n/dates";
import { LoadingState } from "./LoadingState";

/**
 * The signature block (TF-219 AC1, ADR-0051): for each signature, the signer's full name, the date and time with the
 * zone named, and the meaning, as on paper; with the version it applies to and its hash checked against the snapshot it
 * keeps. When the record has changed since, it says so and links to what was signed (TF-063 AC3). The same block
 * prints with the record.
 */
export function SignatureBlock({ signatures }: { signatures: readonly Signature[] }) {
  const { t } = useTranslation();
  return (
    <Stack gap="xs" component="section" aria-labelledby="signature-block-title" data-signature-block>
      <Group gap="xs">
        <IconSignature size={18} aria-hidden="true" />
        <Title order={3} size="h4" id="signature-block-title">
          {t("signatures.title")}
        </Title>
      </Group>
      <Stack gap="sm" component="ol" m={0} pl={0} style={{ listStyle: "none" }}>
        {signatures.map((signature) => (
          <SignatureLine key={signature.id} signature={signature} />
        ))}
      </Stack>
    </Stack>
  );
}

function SignatureLine({ signature }: { signature: Signature }) {
  const { t } = useTranslation();
  const meaning = t(`signatures.meanings.${signature.meaning}`, { defaultValue: signature.meaning });
  return (
    <Stack gap={4} component="li" data-signature={signature.id}>
      <Text size="sm">
        <Text span fw={700} data-signer>
          {signature.signer_name}
        </Text>
        {", "}
        <Text span data-signed-at>
          {formatDateTime(signature.signed_at)}
        </Text>
        {", "}
        <Text span fw={600} data-meaning>
          {meaning}
        </Text>
      </Text>
      <Text size="xs" c="dimmed">
        {t("signatures.line", {
          version: signature.record_version,
          login: signature.signer_login_id,
          method: t(`signatures.methods.${signature.method}`, { defaultValue: signature.method }),
        })}
      </Text>
      {signature.comment !== null && <Text size="sm">{t("signatures.comment", { comment: signature.comment })}</Text>}
      <Group gap={6} wrap="nowrap">
        {signature.hash_matches ? (
          <IconCircleCheck size={16} aria-hidden="true" />
        ) : (
          <IconAlertTriangle size={16} aria-hidden="true" />
        )}
        <Text size="xs" data-hash>
          {signature.hash_matches ? t("signatures.hashMatches") : t("signatures.hashDiffers")}
        </Text>
      </Group>
      {signature.record_changed && (
        <Alert variant="light" color="warning" p="xs" data-record-changed>
          <Text size="sm">
            {signature.current_version === null
              ? t("signatures.recordGone", { version: signature.record_version })
              : t("signatures.recordChanged", {
                  version: signature.record_version,
                  current: signature.current_version,
                })}{" "}
            <Anchor component={Link} to={`/signatures/${signature.id}`} size="sm">
              {t("signatures.seeSigned")}
            </Anchor>
          </Text>
        </Alert>
      )}
    </Stack>
  );
}

/** A record's signatures, read from the API; nothing at all while the record has none. */
export function RecordSignatures({ recordType, recordId }: { recordType: string; recordId: string }) {
  const { t } = useTranslation();
  const signatures = useQuery({
    queryKey: [...complianceQueryKey, "signatures", recordType, recordId],
    queryFn: ({ signal }) => fetchSignatures(recordType, recordId, signal),
  });
  if (signatures.isPending) {
    return <LoadingState height={80} />;
  }
  if (signatures.isError) {
    return (
      <Alert color="danger" role="alert">
        {signatures.error instanceof ApiError ? signatures.error.message : t("errors.unexpected")}
        {signatures.error instanceof ApiError && signatures.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: signatures.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  return signatures.data.data.length === 0 ? null : <SignatureBlock signatures={signatures.data.data} />;
}
