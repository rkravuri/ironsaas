import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Group, SegmentedControl, Stack, Text, Textarea } from "@mantine/core";
import { IconCheck, IconChecks, IconX } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { complianceQueryKey, fetchSignableTypes } from "@/api/compliance";
import {
  closeReview,
  confirmItems,
  decideItem,
  fetchReview,
  fetchReviewItems,
  type ItemState,
  type Review,
  type ReviewItem,
} from "@/api/authorization";
import { DataGrid, type DataGridColumn, type ListResult } from "@/components/data-grid";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, FormDialog, LoadingState, type RecordStatus } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { useHistoryOf } from "@/auth/record-history";
import { SigningDialog } from "@/auth/SigningDialog";
import { useResumedSigning } from "@/auth/signing-resume";
import { dueLabel, progressLabel } from "./review-words";
import { formatDate } from "@/i18n/dates";

const states: ItemState[] = ["open", "decided", "all"];
const isState = (value: string): value is ItemState => (states as readonly string[]).includes(value);
const itemSort = [{ field: "unit_name", direction: "asc" as const }];

/**
 * One access review campaign (TF-038) on the record template: its status, due day, owner and progress, then the items
 * the reader decides (the API keeps those at the units where they hold authz.reviews.decide), and the progress of each
 * unit with its escalation. A revoke ends the access at once, so it asks first; confirming a page asks first too.
 */
export function ReviewRecordPage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const queryClient = useQueryClient();
  const history = useHistoryOf("access_review", id === "" ? undefined : id);
  const review = useQuery({
    queryKey: ["authz", "access-reviews", id],
    queryFn: ({ signal }) => fetchReview(id, signal),
  });
  const [closing, setClosing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();

  if (review.isPending) {
    return <LoadingState height={240} />;
  }
  if (review.isError) {
    return (
      <Alert color="danger" role="alert" title={t("reviews.record.failed")}>
        {review.error instanceof ApiError ? review.error.message : t("errors.unexpected")}
        {review.error instanceof ApiError && review.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: review.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }

  const data = review.data;
  const reload = () => queryClient.invalidateQueries({ queryKey: ["authz", "access-reviews"] });
  const close = async () => {
    setBusy(true);
    setFailure(undefined);
    try {
      await closeReview(data.id);
      await reload();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setClosing(false);
    }
  };

  const status: RecordStatus =
    data.status === "closed"
      ? { label: t("reviews.status.closed"), tone: "neutral" }
      : data.overdue
        ? { label: t("reviews.status.overdue"), tone: "warning" }
        : { label: t("reviews.status.open"), tone: "info" };

  return (
    <>
      {failure && (
        <Alert
          color="danger"
          role="alert"
          mb="md"
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={() => setFailure(undefined)}
        >
          {failure.message}
        </Alert>
      )}
      <RecordPage
        header={{
          title: data.name,
          status,
          meta: [
            { label: t("reviews.record.due"), value: dueLabel(data.due_on) },
            { label: t("reviews.record.owner"), value: data.owner.name },
            { label: t("reviews.record.progress"), value: progressLabel(data.progress, t) },
          ],
          actions:
            data.can_manage && data.status === "open" ? (
              <Button variant="default" onClick={() => setClosing(true)}>
                {t("reviews.record.close")}
              </Button>
            ) : undefined,
        }}
        tabs={[
          { value: "items", label: t("reviews.record.tabs.items"), panel: <Items review={data} onDecided={reload} /> },
          { value: "units", label: t("reviews.record.tabs.units"), panel: <Units review={data} /> },
        ]}
        history={history}
      />
      <ConfirmDialog
        opened={closing}
        title={t("reviews.record.closeTitle")}
        message={t("reviews.record.closeMessage", { open: data.progress.open })}
        confirmLabel={t("reviews.record.closeConfirm")}
        busy={busy}
        onConfirm={() => void close()}
        onCancel={() => setClosing(false)}
      />
    </>
  );
}

/** What an item says: who decided what, or who decides it, in words; a signed decision says it was signed. */
function decisionLabel(item: ReviewItem, t: ReturnType<typeof useTranslation>["t"]): string {
  if (!item.decision) {
    if (item.yours) {
      return t("reviews.items.yours");
    }
    return item.with_owner ? t("reviews.items.withOwner") : t("reviews.items.toDecide");
  }
  const decided = t(item.decision.decision === "confirm" ? "reviews.items.confirmedBy" : "reviews.items.revokedBy", {
    name: item.decision.decided_by.name,
  });
  const signature = item.decision.signature;
  return signature
    ? t("reviews.items.signed", {
        decided,
        meaning: t(`signatures.meanings.${signature.meaning}`, { defaultValue: signature.meaning }),
      })
    : decided;
}

/** The access review decision as a signable document type (ADR-0051): its policy says whether decisions are signed. */
const decisionType = "access_review_decision";

/**
 * The items to decide: the grid with a choice of which, Confirm and Revoke for the chosen row, and Confirm for the
 * whole page shown. Revoke asks first, with room for a comment, because it ends the access at once.
 */
function Items({ review, onDecided }: { review: Review; onDecided: () => Promise<void> }) {
  const { t } = useTranslation();
  const [state, setState] = useState<ItemState>("open");
  const [refreshKey, setRefreshKey] = useState(0);
  const [selected, setSelected] = useState<ReviewItem | undefined>();
  const [revoking, setRevoking] = useState<ReviewItem | undefined>();
  // The items the confirmation was opened for: kept after it closes, so its words do not change while it fades out.
  const [confirming, setConfirming] = useState<{ opened: boolean; items: ReviewItem[] }>({ opened: false, items: [] });
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | undefined>();
  const [failure, setFailure] = useState<ApiError | undefined>();
  const [pageOpen, setPageOpen] = useState<ReviewItem[]>([]);
  // Back from a company login's fresh sign-in, the decision waiting for its signature opens again (ADR-0051).
  const resumed = useResumedSigning(decisionType);
  const [signing, setSigning] = useState<{ item: ReviewItem; decision: "confirm" | "revoke" } | undefined>(
    () => resumed?.context as { item: ReviewItem; decision: "confirm" | "revoke" } | undefined,
  );
  const open = review.status === "open";
  const types = useQuery({
    queryKey: [...complianceQueryKey, "types"],
    queryFn: ({ signal }) => fetchSignableTypes(signal),
  });
  // Signed when the tenant's policy says so (TF-063 AC1): each decision then goes through the signing dialog, one by one.
  const signed = types.data?.data.find((type) => type.record_type === decisionType)?.policy.required ?? false;

  const fetchPage = useCallback(
    async (query: string, signal?: AbortSignal): Promise<ListResult<ReviewItem>> => {
      const result = await fetchReviewItems(review.id, state, query, signal);
      // Nobody decides their own access (ADR-0037): the page's confirmation leaves the reader's own rows out.
      setPageOpen(result.data.filter((item) => item.decision === null && !item.yours));
      return result;
    },
    [review.id, state],
  );

  const after = async (message: string) => {
    setSelected(undefined);
    setNotice(message);
    setRefreshKey((key) => key + 1);
    await onDecided();
  };

  const run = async (action: () => Promise<string>) => {
    setBusy(true);
    setFailure(undefined);
    setNotice(undefined);
    try {
      await after(await action());
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  const confirmOne = (item: ReviewItem) =>
    run(async () => {
      await decideItem(review.id, item.id, "confirm");
      return t("reviews.items.confirmed", { name: item.principal_name, role: item.role_name });
    });

  const confirmPage = () =>
    run(async () => {
      const result = await confirmItems(
        review.id,
        confirming.items.map((item) => item.id),
      );
      setConfirming((current) => ({ ...current, opened: false }));
      return result.already_decided > 0
        ? t("reviews.items.confirmedManyWithDecided", { count: result.confirmed, decided: result.already_decided })
        : t("reviews.items.confirmedMany", { count: result.confirmed });
    });

  const columns = useMemo<DataGridColumn<ReviewItem>[]>(
    () => [
      {
        field: "principal_name",
        headerKey: "reviews.items.columns.principal",
        sortable: true,
        filter: "text",
        flex: 2,
      },
      { field: "role_name", headerKey: "reviews.items.columns.role", sortable: true, filter: "text", flex: 2 },
      { field: "unit_name", headerKey: "reviews.items.columns.unit", sortable: true, filter: "text", flex: 1 },
      {
        field: "unit_path",
        headerKey: "reviews.items.columns.path",
        flex: 2,
        format: (item) => item.unit_path.map((step) => step.name).join(" / "),
      },
      {
        field: "scope_kind",
        headerKey: "reviews.items.columns.scope",
        width: 200,
        format: (item) => t(`reviews.items.scopes.${item.scope_kind}`),
      },
      {
        field: "decision",
        headerKey: "reviews.items.columns.decision",
        flex: 2,
        format: (item) => decisionLabel(item, t),
      },
      // An item that takes part in a conflict of the report says so by the rule's name (TF-036): words, not a mark alone.
      {
        field: "conflicts",
        headerKey: "reviews.items.columns.conflicts",
        flex: 2,
        format: (item) =>
          item.conflicts.length > 0
            ? t("reviews.items.conflict", { rules: item.conflicts.join(", ") })
            : t("reviews.items.noConflict"),
      },
    ],
    [t],
  );

  const undecided = selected !== undefined && selected.decision === null && !selected.yours && open;

  return (
    <Stack gap="sm">
      {!open && (
        <Alert role="status" variant="light" color="info">
          {t("reviews.items.closed")}
        </Alert>
      )}
      <Group justify="space-between" wrap="wrap" gap="sm">
        <SegmentedControl
          aria-label={t("reviews.items.which")}
          value={state}
          onChange={(value) => {
            if (isState(value)) {
              setState(value);
              setSelected(undefined);
            }
          }}
          data={states.map((value) => ({ value, label: t(`reviews.items.states.${value}`) }))}
        />
        <Group gap="xs">
          <Button
            variant="default"
            leftSection={<IconCheck size={16} aria-hidden="true" />}
            disabled={!undecided}
            loading={busy && undecided}
            onClick={() => {
              if (selected) {
                if (signed) {
                  setSigning({ item: selected, decision: "confirm" });
                } else {
                  void confirmOne(selected);
                }
              }
            }}
          >
            {t("reviews.items.confirm")}
          </Button>
          <Button
            variant="default"
            color="danger"
            leftSection={<IconX size={16} aria-hidden="true" />}
            disabled={!undecided}
            onClick={() =>
              selected && (signed ? setSigning({ item: selected, decision: "revoke" }) : setRevoking(selected))
            }
          >
            {t("reviews.items.revoke")}
          </Button>
          <Button
            leftSection={<IconChecks size={16} aria-hidden="true" />}
            disabled={!open || pageOpen.length === 0 || signed}
            onClick={() => setConfirming({ opened: true, items: pageOpen })}
          >
            {t("reviews.items.confirmPage", { count: pageOpen.length })}
          </Button>
        </Group>
      </Group>
      <Text size="sm" c="dimmed">
        {t("reviews.items.intro")}
      </Text>
      {signed && open && (
        <Text size="sm" data-signed-decisions>
          {t("reviews.items.signedPolicy")}
        </Text>
      )}
      {notice && (
        <Alert
          role="status"
          variant="light"
          color="success"
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={() => setNotice(undefined)}
        >
          {notice}
        </Alert>
      )}
      {failure && (
        <Alert
          color="danger"
          role="alert"
          withCloseButton
          closeButtonLabel={t("dialog.close")}
          onClose={() => setFailure(undefined)}
        >
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      <div style={{ height: 460 }}>
        <DataGrid<ReviewItem>
          columns={columns}
          fetchPage={fetchPage}
          defaultSort={itemSort}
          label={t("reviews.items.label")}
          refreshKey={`${state}:${refreshKey}`}
          onSelectionChange={setSelected}
        />
      </div>
      <FormDialog
        opened={revoking !== undefined}
        title={t("reviews.items.revokeTitle")}
        onClose={() => setRevoking(undefined)}
      >
        {revoking && (
          <RevokeForm
            item={revoking}
            onClose={() => setRevoking(undefined)}
            onDone={async (comment) => {
              const item = revoking;
              await decideItem(review.id, item.id, "revoke", comment);
              setRevoking(undefined);
              await after(t("reviews.items.revoked", { name: item.principal_name, role: item.role_name }));
            }}
          />
        )}
      </FormDialog>
      <SigningDialog
        opened={signing !== undefined}
        recordType={decisionType}
        title={t(signing?.decision === "revoke" ? "reviews.items.signRevokeTitle" : "reviews.items.signConfirmTitle")}
        summary={
          signing &&
          t(signing.decision === "revoke" ? "reviews.items.revokeMessage" : "reviews.items.signConfirmMessage", {
            name: signing.item.principal_name,
            role: signing.item.role_name,
            unit: signing.item.unit_name,
          })
        }
        meanings={signing?.decision === "revoke" ? ["rejected"] : ["approved"]}
        context={signing}
        initial={resumed}
        onClose={() => setSigning(undefined)}
        onSign={async (credentials, _meaning, comment) => {
          if (signing) {
            const { item, decision } = signing;
            await decideItem(review.id, item.id, decision, comment, credentials);
            setSigning(undefined);
            await after(
              t(decision === "revoke" ? "reviews.items.revoked" : "reviews.items.confirmed", {
                name: item.principal_name,
                role: item.role_name,
              }),
            );
          }
        }}
      />
      <ConfirmDialog
        opened={confirming.opened}
        title={t("reviews.items.confirmPageTitle")}
        message={t("reviews.items.confirmPageMessage", { count: confirming.items.length })}
        confirmLabel={t("reviews.items.confirmPageConfirm", { count: confirming.items.length })}
        busy={busy}
        onConfirm={() => void confirmPage()}
        onCancel={() => setConfirming((current) => ({ ...current, opened: false }))}
      />
    </Stack>
  );
}

function RevokeForm({
  item,
  onClose,
  onDone,
}: {
  item: ReviewItem;
  onClose: () => void;
  onDone: (comment: string | undefined) => Promise<void>;
}) {
  const { t } = useTranslation();
  const schema = useMemo(() => z.object({ comment: z.string().max(500, t("reviews.items.commentTooLong")) }), [t]);
  const form = useZodForm({ schema, initialValues: { comment: "" } });
  return (
    <Form
      form={form}
      onSubmit={async (values) => onDone(values.comment.trim() || undefined)}
      submitLabel={t("reviews.items.revokeConfirm")}
      secondary={{ label: t("dialog.cancel"), onClick: onClose }}
    >
      <Text size="sm">
        {t("reviews.items.revokeMessage", { name: item.principal_name, role: item.role_name, unit: item.unit_name })}
      </Text>
      <Textarea
        label={t("reviews.items.comment")}
        description={t("reviews.items.commentHint")}
        autosize
        minRows={2}
        maxRows={5}
        data-autofocus
        {...form.getInputProps("comment")}
      />
    </Form>
  );
}

/** Each unit's progress, and when its reviewers were late, who was told. */
function Units({ review }: { review: Review }) {
  const { t, i18n } = useTranslation();
  if (review.by_unit.length === 0) {
    return (
      <EmptyState drawing="nothingYet" size="sm" title={t("reviews.units.none")} text={t("reviews.units.noneText")} />
    );
  }
  return (
    <Stack gap="sm" component="ul" m={0} pl={0} style={{ listStyle: "none" }} aria-label={t("reviews.units.label")}>
      {review.by_unit.map((unit) => (
        <li key={unit.unit_id}>
          <Text size="sm" fw={600}>
            {unit.unit_path.join(" / ")}
          </Text>
          <Text size="sm">{progressLabel(unit.progress, t)}</Text>
          {unit.escalation && (
            <Text size="xs" c="dimmed">
              {t("reviews.units.escalated", {
                date: formatDate(unit.escalation.at),
                targets: new Intl.ListFormat(i18n.language, { type: "conjunction" }).format(
                  unit.escalation.targets.map((target) => target.name),
                ),
              })}
            </Text>
          )}
        </li>
      ))}
    </Stack>
  );
}
