import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Group, Stack, Table, Text, TextInput, Title } from "@mantine/core";
import { IconPencil, IconPlus, IconTrash } from "@tabler/icons-react";
import { z } from "zod";
import {
  addApp,
  addPasskey,
  addPasskeyOptions,
  confirmApp,
  factorsQueryKey,
  fetchFactors,
  regenerateRecoveryCodes,
  removeFactor,
  renameFactor,
  type Enrolled,
  type Factor,
} from "@/api/identity";
import { FactorEnrolment, ProblemAlert, RecoveryCodesPanel } from "@/auth/mfa";
import { mfaProblem, type ShownProblem } from "@/auth/mfa-problems";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, FormDialog, LoadingState } from "@/components/panels";
import { formatDate, formatDateTime } from "@/i18n/dates";

const when = (value: string) => formatDateTime(value);

const securityEventsKey = ["identity", "me", "security-events"] as const;

/**
 * My profile's second factors (TF-013, ADR-0047): each factor with its name, kind and when it was added and last used;
 * add an authenticator app or a passkey, rename and remove one, and make new recovery codes. Each change is a high-risk
 * action behind a recent sign-in (TF-248): the shared dialog asks the person to confirm it is them when it is older. A
 * sign-in by recovery code lands here with "Add a second factor" open.
 */
export function SecondFactors() {
  const { t } = useTranslation();
  const { me } = useSession();
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const afterRecoveryCode = params.get("add") === "1";
  const [adding, setAdding] = useState(afterRecoveryCode);
  const [codes, setCodes] = useState<readonly string[] | undefined>();
  const [renaming, setRenaming] = useState<Factor | undefined>();
  const [removing, setRemoving] = useState<Factor | undefined>();
  const [regenerating, setRegenerating] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<ShownProblem | undefined>();
  const [saved, setSaved] = useState(false);
  const factors = useQuery({ queryKey: factorsQueryKey, queryFn: ({ signal }) => fetchFactors(signal) });

  const changed = () => {
    void queryClient.invalidateQueries({ queryKey: factorsQueryKey });
    void queryClient.invalidateQueries({ queryKey: securityEventsKey });
    void queryClient.invalidateQueries({ queryKey: ["me"] });
  };

  const closeAdding = () => {
    setAdding(false);
    if (afterRecoveryCode) {
      params.delete("add");
      setParams(params, { replace: true });
    }
  };

  const enrolled = (result: Enrolled) => {
    closeAdding();
    setSaved(true);
    changed();
    if (result.recovery_codes && result.recovery_codes.length > 0) {
      setCodes(result.recovery_codes);
    }
  };

  const act = async (action: () => Promise<void>) => {
    setBusy(true);
    setProblem(undefined);
    setSaved(false);
    try {
      await action();
      changed();
    } catch (error) {
      setProblem(mfaProblem(error, t));
    } finally {
      setBusy(false);
      setRemoving(undefined);
      setRegenerating(false);
    }
  };

  const data = factors.data;
  return (
    <Stack gap="sm" data-second-factors>
      <Title order={3} size="h4">
        {t("mfa.profile.title")}
      </Title>
      <Text size="sm" c="dimmed" maw={640}>
        {t("mfa.profile.intro")}
      </Text>
      {afterRecoveryCode && (
        <Alert color="warning" role="status" maw={640}>
          {t("mfa.profile.enrolPrompt")}
        </Alert>
      )}
      {data?.enrol_by && (
        <Alert color="warning" role="status" maw={640}>
          {t("mfa.banner.text", { when: formatDate(data.enrol_by) })}
        </Alert>
      )}
      {data?.required && !data.enrol_by && (
        <Text size="sm" maw={640}>
          {t("mfa.profile.required")}
        </Text>
      )}
      {saved && (
        <Alert color="success" role="status" maw={640}>
          {t("mfa.profile.added")}
        </Alert>
      )}
      {problem && <ProblemAlert problem={problem} />}
      {factors.isPending ? (
        <LoadingState height={120} />
      ) : factors.isError ? (
        <ProblemAlert problem={mfaProblem(factors.error, t)} />
      ) : data && data.factors.length === 0 ? (
        <EmptyState drawing="nothingYet" size="sm" title={t("mfa.profile.none")} text={t("mfa.profile.noneText")} />
      ) : (
        <Table withTableBorder striped>
          <Table.Caption>{t("mfa.profile.caption")}</Table.Caption>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("mfa.profile.columns.name")}</Table.Th>
              <Table.Th>{t("mfa.profile.columns.kind")}</Table.Th>
              <Table.Th>{t("mfa.profile.columns.added")}</Table.Th>
              <Table.Th>{t("mfa.profile.columns.used")}</Table.Th>
              <Table.Th>{t("mfa.profile.columns.actions")}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {data?.factors.map((factor) => (
              <Table.Tr key={factor.id} data-factor={factor.kind}>
                <Table.Td>{factor.name}</Table.Td>
                <Table.Td>{t(`mfa.kinds.${factor.kind}`, { defaultValue: factor.kind })}</Table.Td>
                <Table.Td>{when(factor.created_at)}</Table.Td>
                <Table.Td>{factor.last_used_at ? when(factor.last_used_at) : t("mfa.profile.never")}</Table.Td>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <Button
                      variant="default"
                      size="xs"
                      leftSection={<IconPencil size={14} aria-hidden="true" />}
                      onClick={() => setRenaming(factor)}
                      aria-label={t("mfa.profile.renameNamed", { name: factor.name })}
                    >
                      {t("mfa.profile.rename")}
                    </Button>
                    <Button
                      variant="default"
                      size="xs"
                      color="danger"
                      leftSection={<IconTrash size={14} aria-hidden="true" />}
                      onClick={() => setRemoving(factor)}
                      aria-label={t("mfa.profile.removeNamed", { name: factor.name })}
                    >
                      {t("mfa.profile.remove")}
                    </Button>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {data && data.allowed.length > 0 && (
        <Group>
          <Button
            variant="default"
            leftSection={<IconPlus size={16} aria-hidden="true" />}
            onClick={() => setAdding(true)}
            data-add-factor
          >
            {t("mfa.profile.add")}
          </Button>
        </Group>
      )}

      <Title order={4} size="h5" mt="sm">
        {t("mfa.profile.codesTitle")}
      </Title>
      {data && data.factors.length > 0 ? (
        <Group gap="md" align="center">
          <Text size="sm" data-codes-left={data.recovery_codes_left}>
            {t("mfa.profile.codesLeft", { count: data.recovery_codes_left })}
          </Text>
          <Button variant="default" size="xs" onClick={() => setRegenerating(true)} data-regenerate-codes>
            {t("mfa.profile.regenerate")}
          </Button>
        </Group>
      ) : (
        <Text size="sm" c="dimmed">
          {t("mfa.profile.codesNone")}
        </Text>
      )}

      {adding && data && (
        <FormDialog opened title={t("mfa.profile.addTitle")} onClose={closeAdding} size="lg">
          <FactorEnrolment
            allowed={data.allowed}
            calls={{
              startApp: addApp,
              confirmApp,
              passkeyOptions: async () => (await addPasskeyOptions()).options,
              registerPasskey: addPasskey,
            }}
            onEnrolled={enrolled}
          />
        </FormDialog>
      )}
      {codes && (
        <FormDialog opened title={t("mfa.codes.title")} onClose={() => setCodes(undefined)} size="lg">
          <RecoveryCodesPanel
            codes={codes}
            account={me?.login_id ?? ""}
            onDone={() => setCodes(undefined)}
            doneLabel={t("mfa.codes.done")}
          />
        </FormDialog>
      )}
      {renaming && (
        <RenameDialog
          factor={renaming}
          onClose={() => setRenaming(undefined)}
          onRenamed={() => {
            setRenaming(undefined);
            changed();
          }}
        />
      )}
      {removing && (
        <ConfirmDialog
          opened
          title={t("mfa.profile.removeTitle", { name: removing.name })}
          message={t("mfa.profile.removeMessage", { name: removing.name })}
          confirmLabel={t("mfa.profile.removeConfirm")}
          danger
          busy={busy}
          onConfirm={() => void act(() => removeFactor(removing.id).then(() => undefined))}
          onCancel={() => setRemoving(undefined)}
        />
      )}
      {regenerating && (
        <ConfirmDialog
          opened
          title={t("mfa.profile.regenerateTitle")}
          message={t("mfa.profile.regenerateMessage")}
          confirmLabel={t("mfa.profile.regenerateConfirm")}
          busy={busy}
          onConfirm={() => void act(async () => setCodes((await regenerateRecoveryCodes()).codes))}
          onCancel={() => setRegenerating(false)}
        />
      )}
    </Stack>
  );
}

interface RenameValues extends Record<string, unknown> {
  name: string;
}

function RenameDialog({ factor, onClose, onRenamed }: { factor: Factor; onClose: () => void; onRenamed: () => void }) {
  const { t } = useTranslation();
  const schema = useMemo(
    () => z.object({ name: z.string().trim().min(1, t("mfa.profile.nameRequired")).max(100) }),
    [t],
  );
  const form = useZodForm<RenameValues>({ schema, initialValues: { name: factor.name } });
  // A refusal is the design system's form's to show, the API's words with the correlation id.
  const submit = async (values: RenameValues) => {
    await renameFactor(factor.id, values.name.trim());
    onRenamed();
  };
  return (
    <FormDialog opened title={t("mfa.profile.renameTitle", { name: factor.name })} onClose={onClose}>
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("mfa.profile.renameSubmit")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <TextInput label={t("mfa.app.name")} required data-autofocus maxLength={100} {...form.getInputProps("name")} />
      </Form>
    </FormDialog>
  );
}
