import { useMemo, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Button, Group, Select, Stack, Table, Text, TextInput, Textarea } from "@mantine/core";
import { IconLock, IconLockOpen, IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { auditQueryKey, fetchLegalHolds, placeLegalHold, releaseLegalHold, type LegalHold } from "@/api/audit";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState, ReasonDialog, RecordHistoryButton } from "@/components/panels";
import { ConsolePage } from "@/components/templates";
import { formatDateTime } from "@/i18n/dates";

export const manageHolds = "audit.legal_holds.manage";
const holdsKey = [...auditQueryKey, "legal-holds"] as const;
const scopes = ["tenant", "record", "master"] as const;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

interface PlaceValues extends Record<string, unknown> {
  scope: string;
  recordType: string;
  recordId: string;
  reason: string;
}

/** What a hold keeps, in words: the whole tenant, one record, or every record of a master. */
function scopeWords(hold: LegalHold, t: TFunction): string {
  return hold.scope === "tenant"
    ? t("legalHolds.scopes.tenantText")
    : t(`legalHolds.scopes.${hold.scope}Text`, { type: hold.record_type ?? "", id: hold.record_id ?? "" });
}

/**
 * Legal holds (TF-066 AC2, ADR-0050): a hold keeps records the retention would delete, on the whole tenant (the audit
 * trail's oldest end too), one record, or every record of a master such as a supplier. Placing one needs the critical
 * audit.legal_holds.manage, a reason and a recent sign-in; releasing one needs a reason too, and the hold stays listed.
 */
export function LegalHoldsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const canManage = session.permissions.includes(manageHolds);
  const holds = useQuery({ queryKey: holdsKey, queryFn: ({ signal }) => fetchLegalHolds(signal) });
  const [placing, setPlacing] = useState(false);
  const [releasing, setReleasing] = useState<LegalHold | undefined>();
  const [done, setDone] = useState<string | undefined>();

  const refresh = () => queryClient.invalidateQueries({ queryKey: holdsKey });
  const all = holds.data?.holds ?? [];
  const standing = all.filter((hold) => hold.released_at === null);
  const released = all.filter((hold) => hold.released_at !== null);

  const table = (rows: LegalHold[], kind: "standing" | "released") => (
    <Table withTableBorder striped data-legal-holds={kind}>
      <Table.Caption>{t(`legalHolds.${kind}Caption`, { count: rows.length })}</Table.Caption>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t("legalHolds.columns.scope")}</Table.Th>
          <Table.Th>{t("legalHolds.columns.reason")}</Table.Th>
          <Table.Th>{t("legalHolds.columns.placed")}</Table.Th>
          {kind === "released" ? (
            <Table.Th>{t("legalHolds.columns.released")}</Table.Th>
          ) : (
            canManage && <Table.Th>{t("legalHolds.columns.actions")}</Table.Th>
          )}
          <Table.Th>{t("legalHolds.columns.history")}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {rows.map((hold) => (
          <Table.Tr key={hold.id}>
            <Table.Td>
              <Badge
                {...(kind === "standing" ? { variant: "light", color: "warning" } : { variant: "default" })}
                leftSection={
                  kind === "standing" ? (
                    <IconLock size={12} aria-hidden="true" />
                  ) : (
                    <IconLockOpen size={12} aria-hidden="true" />
                  )
                }
              >
                {t(`legalHolds.scopes.${hold.scope}`)}
              </Badge>
              <Text size="sm" mt={4} style={{ overflowWrap: "anywhere" }}>
                {scopeWords(hold, t)}
              </Text>
            </Table.Td>
            <Table.Td>
              <Text size="sm">{hold.reason}</Text>
            </Table.Td>
            <Table.Td>
              <Text size="sm">{formatDateTime(hold.placed_at)}</Text>
              <Text size="xs" c="dimmed">
                {hold.placed_by_name ?? hold.placed_by}
              </Text>
            </Table.Td>
            {kind === "released" ? (
              <Table.Td>
                <Text size="sm">{hold.released_at ? formatDateTime(hold.released_at) : ""}</Text>
                <Text size="xs" c="dimmed">
                  {hold.release_reason}
                </Text>
              </Table.Td>
            ) : (
              canManage && (
                <Table.Td>
                  <Button
                    variant="subtle"
                    size="compact-sm"
                    onClick={() => setReleasing(hold)}
                    aria-label={t("legalHolds.releaseNamed", { what: scopeWords(hold, t) })}
                  >
                    {t("legalHolds.release")}
                  </Button>
                </Table.Td>
              )
            )}
            <Table.Td>
              <RecordHistoryButton
                compact
                record={{ recordType: "legal_hold", recordId: hold.id }}
                name={scopeWords(hold, t)}
              />
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );

  return (
    <>
      <ConsolePage
        title={t("legalHolds.title")}
        description={t("legalHolds.description")}
        sections={[
          {
            id: "standing",
            title: t("legalHolds.standingTitle"),
            description: t("legalHolds.standingDescription"),
            children: (
              <Stack gap="sm">
                {canManage && (
                  <Group justify="flex-end">
                    <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setPlacing(true)}>
                      {t("legalHolds.place")}
                    </Button>
                  </Group>
                )}
                {done && (
                  <Alert
                    color="success"
                    role="status"
                    withCloseButton
                    closeButtonLabel={t("dialog.close")}
                    onClose={() => setDone(undefined)}
                  >
                    {done}
                  </Alert>
                )}
                {holds.isPending ? (
                  <LoadingState height={120} />
                ) : holds.isError ? (
                  <Alert color="danger" role="alert">
                    {holds.error instanceof ApiError ? holds.error.message : t("errors.unexpected")}
                    {holds.error instanceof ApiError && holds.error.correlationId !== undefined && (
                      <Text size="xs" c="dimmed" mt={4}>
                        {t("errors.correlation", { id: holds.error.correlationId })}
                      </Text>
                    )}
                  </Alert>
                ) : standing.length === 0 ? (
                  <Text size="sm" c="dimmed">
                    {t("legalHolds.noneStanding")}
                  </Text>
                ) : (
                  table(standing, "standing")
                )}
              </Stack>
            ),
          },
          {
            id: "released",
            title: t("legalHolds.releasedTitle"),
            description: t("legalHolds.releasedDescription"),
            children:
              released.length === 0 ? (
                <Text size="sm" c="dimmed">
                  {t("legalHolds.noneReleased")}
                </Text>
              ) : (
                table(released, "released")
              ),
          },
        ]}
      />
      {placing && (
        <PlaceHoldDialog
          onClose={() => setPlacing(false)}
          onPlaced={async () => {
            setPlacing(false);
            setDone(t("legalHolds.placed"));
            await refresh();
          }}
        />
      )}
      <ReasonDialog
        opened={releasing !== undefined}
        title={t("legalHolds.releaseTitle")}
        message={releasing ? t("legalHolds.releaseMessage", { what: scopeWords(releasing, t) }) : ""}
        reasonLabel={t("legalHolds.releaseReason")}
        reasonHint={t("legalHolds.reasonHint")}
        confirmLabel={t("legalHolds.release")}
        onClose={() => setReleasing(undefined)}
        onSubmit={async (reason) => {
          if (!releasing) {
            return;
          }
          await releaseLegalHold(releasing.id, reason);
          setReleasing(undefined);
          setDone(t("legalHolds.released"));
          await refresh();
        }}
      />
    </>
  );
}

/** A new hold: what it keeps, and why. The API asks for a recent sign-in, and the shell's dialog asks for it. */
function PlaceHoldDialog({ onClose, onPlaced }: { onClose: () => void; onPlaced: () => Promise<void> }) {
  const { t } = useTranslation();
  const schema = useMemo(
    () =>
      z
        .object({
          scope: z.enum(scopes),
          recordType: z.string().trim().max(100),
          recordId: z.string().trim(),
          reason: z.string().trim().min(1, t("reason.required")).max(500),
        })
        .superRefine((values, context) => {
          if (values.scope === "tenant") {
            return;
          }
          if (values.recordType === "") {
            context.addIssue({ code: "custom", path: ["recordType"], message: t("legalHolds.form.typeRequired") });
          }
          if (!uuid.test(values.recordId)) {
            context.addIssue({ code: "custom", path: ["recordId"], message: t("legalHolds.form.idRule") });
          }
        }),
    [t],
  );
  const form = useZodForm<PlaceValues>({
    schema,
    initialValues: { scope: "record", recordType: "", recordId: "", reason: "" },
  });
  const scope = form.getValues().scope;
  const submit = async (values: PlaceValues) => {
    await placeLegalHold({
      scope: values.scope,
      ...(values.scope === "tenant"
        ? {}
        : { record_type: values.recordType.trim(), record_id: values.recordId.trim() }),
      reason: values.reason.trim(),
    });
    await onPlaced();
  };
  return (
    <FormDialog opened title={t("legalHolds.form.title")} onClose={onClose}>
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("legalHolds.form.confirm")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{t("legalHolds.form.message")}</Text>
        <Select
          label={t("legalHolds.form.scope")}
          data={scopes.map((value) => ({ value, label: t(`legalHolds.scopes.${value}`) }))}
          allowDeselect={false}
          required
          {...form.getInputProps("scope")}
        />
        {scope !== "tenant" && (
          <>
            <TextInput
              label={t("legalHolds.form.recordType")}
              description={t(scope === "master" ? "legalHolds.form.masterTypeHint" : "legalHolds.form.recordTypeHint")}
              required
              {...form.getInputProps("recordType")}
            />
            <TextInput
              label={t("legalHolds.form.recordId")}
              description={t("legalHolds.form.recordIdHint")}
              required
              {...form.getInputProps("recordId")}
            />
          </>
        )}
        <Textarea
          label={t("legalHolds.form.reason")}
          description={t("legalHolds.reasonHint")}
          required
          autosize
          minRows={2}
          maxLength={500}
          {...form.getInputProps("reason")}
        />
      </Form>
    </FormDialog>
  );
}
