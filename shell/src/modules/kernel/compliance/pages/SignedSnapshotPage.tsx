import { useTranslation } from "react-i18next";
import { useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Code, Stack, Table, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import { complianceQueryKey, fetchSignedSnapshot, type SignedSnapshot } from "@/api/compliance";
import { LoadingState, SignatureBlock, type RecordStatus } from "@/components/panels";
import { fieldWords } from "@/components/panels/field-words";
import { RecordPage } from "@/components/templates";

const valueText = (value: unknown) =>
  value === null || value === undefined ? null : typeof value === "string" ? value : JSON.stringify(value);

/**
 * What a signature signed (TF-063 AC2, AC3; ADR-0051): the record's content at the version signed, as its module wrote
 * it, with the signature itself. The hash is recomputed from the kept snapshot and shown beside the hash kept, so a
 * reader sees it matches; the canonical text is there for an auditor to hash with their own tools.
 */
export function SignedSnapshotPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const snapshot = useQuery({
    queryKey: [...complianceQueryKey, "snapshot", id],
    queryFn: ({ signal }) => fetchSignedSnapshot(id, signal),
  });
  if (snapshot.isPending) {
    return <LoadingState height={240} />;
  }
  if (snapshot.isError) {
    return (
      <Alert color="danger" role="alert" title={t("signedSnapshot.failed")}>
        {snapshot.error instanceof ApiError ? snapshot.error.message : t("errors.unexpected")}
        {snapshot.error instanceof ApiError && snapshot.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: snapshot.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const data = snapshot.data;
  const matches = data.recomputed_hash === data.signature.snapshot_hash;
  const status: RecordStatus = matches
    ? { label: t("signedSnapshot.matches"), tone: "success" }
    : { label: t("signedSnapshot.differs"), tone: "danger" };
  return (
    <RecordPage
      header={{
        title: t("signedSnapshot.title", { type: data.type_name }),
        code: data.signature.record_id,
        status,
        meta: [{ label: t("signedSnapshot.version"), value: String(data.signature.record_version) }],
      }}
      tabs={[
        { value: "content", label: t("signedSnapshot.content"), panel: <Content snapshot={data} /> },
        { value: "form", label: t("signedSnapshot.form"), panel: <Canonical snapshot={data} /> },
      ]}
    />
  );
}

function Content({ snapshot }: { snapshot: SignedSnapshot }) {
  const { t } = useTranslation();
  return (
    <Stack gap="md">
      <SignatureBlock signatures={[snapshot.signature]} />
      <Text size="sm">{t("signedSnapshot.contentIntro", { version: snapshot.signature.record_version })}</Text>
      <Table withTableBorder variant="vertical" layout="fixed" data-snapshot-content>
        <Table.Tbody>
          {Object.entries(snapshot.content).map(([field, value]) => {
            const text = valueText(value);
            return (
              <Table.Tr key={field}>
                <Table.Th w={240}>{fieldWords(field)}</Table.Th>
                <Table.Td style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
                  {text ?? (
                    <Text span size="sm" c="dimmed">
                      {t("history.changes.none")}
                    </Text>
                  )}
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

function Canonical({ snapshot }: { snapshot: SignedSnapshot }) {
  const { t } = useTranslation();
  return (
    <Stack gap="sm">
      <Text size="sm">{t("signedSnapshot.formIntro", { form: snapshot.form })}</Text>
      <Table withTableBorder variant="vertical" layout="fixed">
        <Table.Tbody>
          <Table.Tr>
            <Table.Th w={240}>{t("signedSnapshot.hashKept")}</Table.Th>
            <Table.Td>
              <Code data-hash-kept style={{ overflowWrap: "anywhere" }}>
                {snapshot.signature.snapshot_hash}
              </Code>
            </Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("signedSnapshot.hashNow")}</Table.Th>
            <Table.Td>
              <Code data-hash-now style={{ overflowWrap: "anywhere" }}>
                {snapshot.recomputed_hash}
              </Code>
            </Table.Td>
          </Table.Tr>
        </Table.Tbody>
      </Table>
      <Code block style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }} data-canonical>
        {snapshot.canonical}
      </Code>
    </Stack>
  );
}
