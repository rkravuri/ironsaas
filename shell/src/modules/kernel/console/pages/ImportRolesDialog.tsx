import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, FileInput, Group, List, Modal, Stack, Text, Textarea, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { importRoles, type ImportDiff } from "@/api/authorization";

interface Failure {
  message: string;
  correlationId: string | undefined;
}

/**
 * Roles from another tenant's export (TF-032 AC3): the file or its text first, then the dry run's diff, and only then
 * the apply. Unknown permissions are shown and stop the apply; system roles are never imported.
 */
export function ImportRolesDialog({ onClose, onApplied }: { onClose: () => void; onApplied: () => void }) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [document, setDocument] = useState<unknown>();
  const [diff, setDiff] = useState<ImportDiff | undefined>();
  const [failure, setFailure] = useState<Failure | undefined>();
  const [busy, setBusy] = useState(false);

  const fail = (error: unknown) =>
    setFailure(error instanceof ApiError ? { message: error.message, correlationId: error.correlationId } : { message: t("errors.unexpected"), correlationId: undefined });

  const check = async () => {
    setFailure(undefined);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      setFailure({ message: t("roles.import.notJson"), correlationId: undefined });
      return;
    }
    setBusy(true);
    try {
      setDiff(await importRoles(parsed, true));
      setDocument(parsed);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    setFailure(undefined);
    setBusy(true);
    try {
      await importRoles(document, false);
      onApplied();
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const nothingToDo = diff !== undefined && diff.created.length === 0 && diff.changed.length === 0;
  const blocked = diff !== undefined && diff.unknown.length > 0;

  return (
    <Modal opened onClose={onClose} title={t("roles.import.title")} size="lg" centered closeButtonProps={{ "aria-label": t("dialog.close") }}>
      <Stack>
        {diff === undefined ? (
          <>
            <Text size="sm">{t("roles.import.intro")}</Text>
            <FileInput
              label={t("roles.import.file")}
              accept="application/json,.json"
              clearable
              onChange={(file) => {
                if (file) {
                  void file.text().then(setText);
                }
              }}
            />
            <Textarea label={t("roles.import.paste")} value={text} onChange={(event) => setText(event.currentTarget.value)} autosize minRows={6} maxRows={14} ff="monospace" data-autofocus />
          </>
        ) : (
          <DiffView diff={diff} />
        )}
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
        <Group justify="flex-end">
          {diff === undefined ? (
            <>
              <Button variant="default" onClick={onClose}>
                {t("dialog.cancel")}
              </Button>
              <Button onClick={() => void check()} loading={busy} disabled={text.trim() === ""}>
                {t("roles.import.check")}
              </Button>
            </>
          ) : (
            <>
              <Button variant="default" onClick={() => setDiff(undefined)} disabled={busy}>
                {t("roles.import.back")}
              </Button>
              <Button onClick={() => void apply()} loading={busy} disabled={blocked || nothingToDo}>
                {t("roles.import.apply")}
              </Button>
            </>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}

/** The dry run in words: what the import creates, changes, leaves, cannot take, and skips. */
function DiffView({ diff }: { diff: ImportDiff }) {
  const { t } = useTranslation();
  return (
    <Stack gap="sm" aria-live="polite">
      <Text size="sm">{t("roles.import.dryRun")}</Text>
      {diff.unknown.length > 0 && (
        <Alert color="danger" title={t("roles.import.unknownTitle")}>
          <Text size="sm">{t("roles.import.unknownText")}</Text>
          <List size="sm" mt={4}>
            {diff.unknown.map((role) => (
              <List.Item key={role.code}>
                <Text span ff="monospace" size="sm">
                  {role.code}
                </Text>
                : {role.permissions.join(", ")}
              </List.Item>
            ))}
          </List>
        </Alert>
      )}
      <Section title={t("roles.import.created", { count: diff.created.length })} items={diff.created} />
      {diff.changed.length > 0 && (
        <Stack gap={4}>
          <Title order={4}>{t("roles.import.changed", { count: diff.changed.length })}</Title>
          <List size="sm">
            {diff.changed.map((change) => (
              <List.Item key={change.code}>
                <Text span ff="monospace" size="sm">
                  {change.code}
                </Text>
                {change.added.length > 0 && <Text size="sm">{t("roles.import.gains", { permissions: change.added.join(", ") })}</Text>}
                {change.removed.length > 0 && <Text size="sm">{t("roles.import.loses", { permissions: change.removed.join(", ") })}</Text>}
                {change.fields.length > 0 && (
                  <Text size="sm">{t("roles.import.fields", { fields: change.fields.map((field) => t(`roles.import.field.${field}`, { defaultValue: field })).join(", ") })}</Text>
                )}
              </List.Item>
            ))}
          </List>
        </Stack>
      )}
      <Section title={t("roles.import.unchanged", { count: diff.unchanged.length })} items={diff.unchanged} />
      <Section title={t("roles.import.skipped", { count: diff.skipped_system.length })} items={diff.skipped_system} note={t("roles.import.skippedNote")} />
    </Stack>
  );
}

function Section({ title, items, note }: { title: string; items: readonly string[]; note?: string }) {
  if (items.length === 0) {
    return null;
  }
  return (
    <Stack gap={4}>
      <Title order={4}>{title}</Title>
      {note !== undefined && (
        <Text size="sm" c="dimmed">
          {note}
        </Text>
      )}
      <List size="sm">
        {items.map((code) => (
          <List.Item key={code}>
            <Text span ff="monospace" size="sm">
              {code}
            </Text>
          </List.Item>
        ))}
      </List>
    </Stack>
  );
}
