import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Stack, Table, Text } from "@mantine/core";
import { IconDownload } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { complianceQueryKey, fetchEvidencePack, linkEvidencePack, type EvidencePack } from "@/api/compliance";
import { useHistoryOf } from "@/auth/record-history";
import { LoadingState } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { packPeriodWords, packStateTone, packStateWords, sizeWords } from "./pack-words";

const recordType = "evidence_pack";

/** A pack still being made is read again every two seconds; a ready or failed one is final. */
const waiting = (pack: EvidencePack | undefined) =>
  pack !== undefined && (pack.status === "requested" || pack.status === "building");

/**
 * One evidence pack (TF-067, ADR-0052) on the record template: its period, its requester, where it stands, and once made
 * its size, its hash, its manifest's, and the audit chain's head it names with the control plane's anchor of that
 * position, so an auditor can check the pack against the chain. The download asks the API for a link that is the
 * person's alone, works once and lasts five minutes; the API audits it.
 */
export function EvidencePackRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const history = useHistoryOf(recordType, id === "" ? undefined : id);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [downloading, setDownloading] = useState(false);
  const pack = useQuery({
    queryKey: [...complianceQueryKey, "evidence-packs", id],
    queryFn: ({ signal }) => fetchEvidencePack(id, signal),
    refetchInterval: (query) => (waiting(query.state.data) ? 2000 : false),
  });
  if (pack.isPending) {
    return <LoadingState height={240} />;
  }
  if (pack.isError) {
    return (
      <Alert color="danger" role="alert" title={t("evidencePacks.failed")}>
        {pack.error instanceof ApiError ? pack.error.message : t("errors.unexpected")}
        {pack.error instanceof ApiError && pack.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: pack.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const data = pack.data;
  const download = async () => {
    setFailure(undefined);
    setDownloading(true);
    try {
      const link = await linkEvidencePack(data.id);
      // Same origin, with the session: the browser saves the file the answer names and stays on this page.
      const anchor = document.createElement("a");
      anchor.href = link.url;
      anchor.download = "";
      anchor.click();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setDownloading(false);
    }
  };

  return (
    <RecordPage
      header={{
        title: packPeriodWords(data.period_from, data.period_to, t),
        status: { label: packStateWords(data.status, t), tone: packStateTone(data.status) },
        meta: [
          { label: t("evidencePacks.record.requester"), value: data.requester_name },
          { label: t("evidencePacks.record.requested"), value: formatDateTime(data.requested_at) },
        ],
        actions: data.status === "ready" && (
          <Button
            leftSection={<IconDownload size={16} aria-hidden="true" />}
            loading={downloading}
            onClick={() => void download()}
          >
            {t("evidencePacks.record.download")}
          </Button>
        ),
      }}
      tabs={[
        {
          value: "pack",
          label: t("evidencePacks.record.tab"),
          panel: <PackPanel pack={data} failure={failure} />,
        },
      ]}
      history={history}
    />
  );
}

function PackPanel({ pack, failure }: { pack: EvidencePack; failure: ApiError | undefined }) {
  const { t } = useTranslation();
  return (
    <Stack gap="md">
      {failure && (
        <Alert color="danger" role="alert">
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      {waiting(pack) && (
        <Alert color="info" role="status">
          {t("evidencePacks.record.waiting")}
        </Alert>
      )}
      {pack.status === "failed" && (
        <Alert color="danger" role="alert" title={t("evidencePacks.record.failure")}>
          {pack.failure ?? t("errors.unexpected")}
        </Alert>
      )}
      {pack.status === "ready" && (
        <Text size="sm" c="dimmed">
          {t("evidencePacks.record.downloadHint")}
        </Text>
      )}
      <Table withTableBorder variant="vertical" layout="fixed" data-evidence-pack>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th w={220}>{t("evidencePacks.record.documents")}</Table.Th>
            <Table.Td>{t("evidencePacks.record.documentList")}</Table.Td>
          </Table.Tr>
          {pack.made_at !== null && (
            <Table.Tr>
              <Table.Th>{t("evidencePacks.record.made")}</Table.Th>
              <Table.Td>{formatDateTime(pack.made_at)}</Table.Td>
            </Table.Tr>
          )}
          {pack.bytes !== null && (
            <Table.Tr>
              <Table.Th>{t("evidencePacks.record.size")}</Table.Th>
              <Table.Td>{sizeWords(pack.bytes)}</Table.Td>
            </Table.Tr>
          )}
          {pack.sha256 !== null && (
            <Table.Tr>
              <Table.Th>{t("evidencePacks.record.sha256")}</Table.Th>
              <Table.Td ff="monospace" style={{ overflowWrap: "anywhere" }}>
                {pack.sha256}
              </Table.Td>
            </Table.Tr>
          )}
          {pack.manifest_sha256 !== null && (
            <Table.Tr>
              <Table.Th>{t("evidencePacks.record.manifest")}</Table.Th>
              <Table.Td ff="monospace" style={{ overflowWrap: "anywhere" }}>
                {pack.manifest_sha256}
              </Table.Td>
            </Table.Tr>
          )}
          {pack.chain_hash !== null && (
            <>
              <Table.Tr>
                <Table.Th>{t("evidencePacks.record.chain")}</Table.Th>
                <Table.Td ff="monospace" style={{ overflowWrap: "anywhere" }} data-chain-head>
                  {t("evidencePacks.record.chainValue", {
                    segment: pack.chain_segment,
                    position: pack.chain_position,
                    hash: pack.chain_hash,
                  })}
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("evidencePacks.record.anchor")}</Table.Th>
                <Table.Td ff="monospace" style={{ overflowWrap: "anywhere" }}>
                  {pack.anchor_id ?? t("evidencePacks.record.noAnchor")}
                </Table.Td>
              </Table.Tr>
            </>
          )}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}
