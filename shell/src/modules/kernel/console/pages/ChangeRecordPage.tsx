import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Stack, Table, Text } from "@mantine/core";
import { IconArrowBackUp, IconCheck, IconX } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { changesQueryKey, decideChange, fetchChange, withdrawChange, type PendingChange } from "@/api/changes";
import { useHistoryOf } from "@/auth/record-history";
import { useSession } from "@/auth/session-context";
import { SigningDialog } from "@/auth/SigningDialog";
import { useResumedSigning } from "@/auth/signing-resume";
import { ConfirmDialog, LoadingState, ReasonDialog } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";
import { BeforeAfter } from "./BeforeAfter";
import { areaWords, changeStateTone, changeStateWords } from "./change-words";

const recordType = "configuration_change";

type Decision = "approve" | "reject";

/** What a signature waits on: the decision and its reason, kept while a company-login person signs in again. */
interface Signing {
  decision: Decision;
  reason: string;
}

/**
 * One change of sensitive configuration (TF-068, ADR-0052) on the record template: what it changes, before and after
 * side by side, who asked and why, and where it stands. A second administrator who holds the change's permission at its
 * place approves or rejects it with a reason, and signs when the tenant asks for a signature in the change's area; the
 * requester can only withdraw it. A change whose subject changed meanwhile goes back for a new review, and says why.
 */
export function ChangeRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const queryClient = useQueryClient();
  const session = useSession();
  const history = useHistoryOf(recordType, id === "" ? undefined : id);
  const resumed = useResumedSigning(recordType);
  const [deciding, setDeciding] = useState<Decision | undefined>();
  const [signing, setSigning] = useState<Signing | undefined>(() => resumed?.context as Signing | undefined);
  const [withdrawing, setWithdrawing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const change = useQuery({
    queryKey: [...changesQueryKey, id],
    queryFn: ({ signal }) => fetchChange(id, signal),
  });

  const refresh = () => queryClient.invalidateQueries({ queryKey: changesQueryKey });

  /** Sends the decision; a change found stale goes back to review, so the page shows it again as it now stands. */
  const decide = async (decision: Decision, reason: string, signature?: Parameters<typeof decideChange>[3]) => {
    try {
      await decideChange(id, decision, reason, signature);
    } catch (error) {
      await refresh();
      throw error;
    }
    await refresh();
  };

  if (change.isPending) {
    return <LoadingState height={240} />;
  }
  if (change.isError) {
    return (
      <Alert color="danger" role="alert" title={t("changes.failed")}>
        {change.error instanceof ApiError ? change.error.message : t("errors.unexpected")}
        {change.error instanceof ApiError && change.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: change.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const data = change.data;
  const pending = data.status === "pending";
  const mine = data.requested_by === session.me?.id;
  const actions = pending && (
    <>
      {data.may_decide && (
        <>
          <Button
            variant="default"
            leftSection={<IconX size={16} aria-hidden="true" />}
            onClick={() => setDeciding("reject")}
          >
            {t("changes.reject.open")}
          </Button>
          <Button leftSection={<IconCheck size={16} aria-hidden="true" />} onClick={() => setDeciding("approve")}>
            {t("changes.approve.open")}
          </Button>
        </>
      )}
      {mine && (
        <Button
          variant="default"
          leftSection={<IconArrowBackUp size={16} aria-hidden="true" />}
          onClick={() => setWithdrawing(true)}
        >
          {t("changes.withdraw.open")}
        </Button>
      )}
    </>
  );

  return (
    <>
      <RecordPage
        header={{
          title: data.title,
          status: { label: changeStateWords(data.status, t), tone: changeStateTone(data.status) },
          meta: [
            { label: t("changes.record.area"), value: areaWords(data.area, t) },
            { label: t("changes.record.requester"), value: data.requester_name },
            { label: t("changes.record.requested"), value: formatDateTime(data.requested_at) },
            ...(pending ? [{ label: t("changes.record.expires"), value: formatDateTime(data.expires_at) }] : []),
          ],
          actions,
        }}
        signatures={data.signature_id !== null ? { recordType, recordId: data.id } : undefined}
        tabs={[
          {
            value: "change",
            label: t("changes.record.tab"),
            panel: <ChangePanel change={data} mine={mine} failure={failure} />,
          },
        ]}
        history={history}
      />
      <ReasonDialog
        opened={deciding !== undefined}
        title={t(deciding === "reject" ? "changes.reject.title" : "changes.approve.title")}
        message={
          deciding === "reject"
            ? t("changes.reject.message")
            : t(data.signed ? "changes.approve.messageSigned" : "changes.approve.message")
        }
        reasonLabel={t(deciding === "reject" ? "changes.reject.reason" : "changes.approve.reason")}
        reasonHint={t("changes.reasonHint")}
        confirmLabel={t(
          data.signed
            ? "changes.continueToSign"
            : deciding === "reject"
              ? "changes.reject.confirm"
              : "changes.approve.confirm",
        )}
        onClose={() => setDeciding(undefined)}
        onSubmit={async (reason) => {
          const decision = deciding ?? "approve";
          if (data.signed) {
            setDeciding(undefined);
            setSigning({ decision, reason });
            return;
          }
          await decide(decision, reason);
          setDeciding(undefined);
        }}
      />
      <SigningDialog
        opened={signing !== undefined}
        recordType={recordType}
        title={t(signing?.decision === "reject" ? "changes.reject.signTitle" : "changes.approve.signTitle")}
        summary={signing && t("changes.signSummary", { title: data.title, reason: signing.reason })}
        meanings={[signing?.decision === "reject" ? "rejected" : "approved"]}
        comment={false}
        context={signing}
        initial={resumed}
        onClose={() => setSigning(undefined)}
        onSign={async (credentials) => {
          if (signing) {
            await decide(signing.decision, signing.reason, credentials);
            setSigning(undefined);
          }
        }}
      />
      <ConfirmDialog
        opened={withdrawing}
        title={t("changes.withdraw.title")}
        message={t("changes.withdraw.message", { title: data.title })}
        confirmLabel={t("changes.withdraw.confirm")}
        cancelLabel={t("changes.withdraw.keep")}
        busy={busy}
        onCancel={() => setWithdrawing(false)}
        onConfirm={() => {
          setFailure(undefined);
          setBusy(true);
          void withdrawChange(data.id)
            .then(refresh)
            .catch((error: unknown) => setFailure(error instanceof ApiError ? error : undefined))
            .finally(() => {
              setBusy(false);
              setWithdrawing(false);
            });
        }}
      />
    </>
  );
}

/** The change itself: the reason it was asked for, a new review's cause, before and after, and how it was decided. */
function ChangePanel({
  change,
  mine,
  failure,
}: {
  change: PendingChange;
  mine: boolean;
  failure: ApiError | undefined;
}) {
  const { t } = useTranslation();
  const pending = change.status === "pending";
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
      {pending && change.restart_note !== null && (
        <Alert color="warning" role="status" title={t("changes.record.restarted")} data-restarted>
          {change.restart_note}
        </Alert>
      )}
      {pending && (
        <Text size="sm" data-may-decide={change.may_decide}>
          {mine
            ? t("changes.record.yours")
            : change.may_decide
              ? t(change.signed ? "changes.record.decideSigned" : "changes.record.decide")
              : t("changes.record.notYours")}
        </Text>
      )}
      <Table withTableBorder variant="vertical" layout="fixed" data-change-facts>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th w={200}>{t("changes.record.reason")}</Table.Th>
            <Table.Td style={{ whiteSpace: "pre-wrap" }}>{change.reason ?? t("changes.record.noReason")}</Table.Td>
          </Table.Tr>
          {change.decided_at !== null && (
            <>
              <Table.Tr>
                <Table.Th>{t("changes.record.decider")}</Table.Th>
                <Table.Td>
                  {t("changes.record.decidedBy", {
                    name: change.decider_name ?? "",
                    at: formatDateTime(change.decided_at),
                  })}
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th>{t("changes.record.decision")}</Table.Th>
                <Table.Td style={{ whiteSpace: "pre-wrap" }}>
                  {change.decision_reason ?? t("changes.record.noReason")}
                </Table.Td>
              </Table.Tr>
            </>
          )}
        </Table.Tbody>
      </Table>
      <BeforeAfter before={change.before} after={change.after} />
    </Stack>
  );
}
