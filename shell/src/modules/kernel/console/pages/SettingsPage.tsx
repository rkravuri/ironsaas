import { useMemo, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Code,
  Group,
  Modal,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Textarea,
} from "@mantine/core";
import { IconHistory, IconPencil, IconRestore } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { fetchUnits } from "@/api/authorization";
import { fetchSettingHistory, fetchSettings, resetSetting, saveSetting, type SettingView } from "@/api/settings";
import { useAccessAt } from "@/auth/access";
import { contextQueryKey } from "@/auth/entitlements";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, FormDialog, HistoryPanel, LoadingState } from "@/components/panels";
import { ConsolePage, type ConsoleSection } from "@/components/templates";
import { Mandala } from "@/design/patterns";
import { displayValue, settingName, settingSlug as slug, sourceSentence } from "./setting-words";
import { UnitPicker } from "./UnitPicker";

const manage = "settings.values.manage";
const unitsQueryKey = ["authz", "organisation-units"] as const;

type Place = { scope: "tenant" } | { scope: "unit"; unitId: string };

/**
 * The tenant's settings on the console template (TF-057, TF-058, TF-221; ADR-0036): grouped by module, each with its
 * description, its default, the value at the chosen place and where that value comes from, a change in a form dialog
 * with the server's rule on the field, a reset behind a confirmation, and the history. The place is the whole
 * organisation or a unit chosen on the tree; changing needs settings.values.manage there, and the page offers Change
 * and Reset only where the API will allow them.
 */
export function SettingsPage() {
  const { t } = useTranslation();
  const session = useSession();
  const [mode, setMode] = useState<"tenant" | "unit">("tenant");
  const [unitId, setUnitId] = useState("");
  const place: Place | undefined =
    mode === "tenant" ? { scope: "tenant" } : unitId !== "" ? { scope: "unit", unitId } : undefined;
  const units = useQuery({
    queryKey: unitsQueryKey,
    queryFn: ({ signal }) => fetchUnits(signal),
    enabled: mode === "unit",
  });
  const settings = useQuery({
    queryKey: ["settings", place?.scope ?? "none", place?.scope === "unit" ? place.unitId : null],
    queryFn: ({ signal }) => fetchSettings(place as Place, signal),
    enabled: place !== undefined,
  });
  const access = useAccessAt(place?.scope === "unit" ? { unit: place.unitId } : undefined, [manage]);
  const canManage =
    place?.scope === "tenant" ? session.tenantPermissions.includes(manage) : !access.pending && access.can(manage);

  const where = (
    <Stack gap="sm">
      <Group gap="md" wrap="nowrap" align="center">
        {/* The place's own mark, drawn from its id: it changes with the place, so the eye sees the page moved. */}
        <Mandala
          symmetry={6}
          rings={3}
          stroke={2}
          density={0.5}
          seed={`settings-${place?.scope === "unit" ? place.unitId : "tenant"}`}
          size={40}
          accent="console"
        />
        <SegmentedControl
          aria-label={t("settings.where.label")}
          value={mode}
          onChange={(value) => setMode(value === "unit" ? "unit" : "tenant")}
          data={[
            { value: "tenant", label: t("settings.where.tenant") },
            { value: "unit", label: t("settings.where.unit") },
          ]}
        />
      </Group>
      {mode === "unit" &&
        (units.isPending ? (
          <LoadingState height={120} />
        ) : units.isError ? (
          <Alert color="warning" role="alert">
            {t("settings.where.unitsFailed")}
          </Alert>
        ) : (
          <UnitPicker
            label={t("settings.where.unitLabel")}
            units={units.data.data}
            value={unitId}
            onChange={setUnitId}
          />
        ))}
    </Stack>
  );

  const sections: ConsoleSection[] = [
    {
      id: "settings-where",
      title: t("settings.where.title"),
      description: t("settings.where.description"),
      children: where,
    },
  ];
  if (place === undefined) {
    sections.push({
      id: "settings-values",
      title: t("settings.values"),
      children: <Text size="sm">{t("settings.where.pickUnit")}</Text>,
    });
  } else if (settings.isPending) {
    sections.push({ id: "settings-values", title: t("settings.values"), children: <LoadingState height={160} /> });
  } else if (settings.isError) {
    const failure = settings.error instanceof ApiError ? settings.error : undefined;
    sections.push({
      id: "settings-values",
      title: t("settings.values"),
      children: (
        <Alert color="danger" role="alert" title={t("settings.failed")}>
          {failure?.message ?? t("errors.unexpected")}
          {failure?.correlationId !== undefined && (
            <Text size="xs" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      ),
    });
  } else if (settings.data.data.length === 0) {
    sections.push({
      id: "settings-values",
      title: t("settings.values"),
      children: <EmptyState drawing="nothingYet" size="sm" title={t("settings.empty")} />,
    });
  } else {
    const placeLabel =
      place.scope === "tenant" ? t("settings.where.tenant") : (settings.data.unit?.path ?? t("settings.where.unit"));
    for (const [module, group] of byModule(settings.data.data)) {
      sections.push({
        id: `settings-${module}`,
        title: t(`settings.modules.${module}`, { defaultValue: module }),
        children: (
          <Stack gap="lg">
            {group.map((setting) => (
              <SettingRow
                key={setting.key}
                setting={setting}
                place={place}
                placeLabel={placeLabel}
                canManage={canManage}
              />
            ))}
          </Stack>
        ),
      });
    }
  }

  return <ConsolePage title={t("settings.title")} description={t("settings.description")} sections={sections} />;
}

function byModule(settings: readonly SettingView[]): Map<string, SettingView[]> {
  const groups = new Map<string, SettingView[]>();
  for (const setting of settings) {
    groups.set(setting.module, [...(groups.get(setting.module) ?? []), setting]);
  }
  return groups;
}

function SettingRow({
  setting,
  place,
  placeLabel,
  canManage,
}: {
  setting: SettingView;
  place: Place;
  placeLabel: string;
  canManage: boolean;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [history, setHistory] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const name = settingName(setting, t);
  const titleId = `setting-${slug(setting.key)}`;

  const refresh = async () => {
    // The shell's own values (the session limits) come with /me/context, so it is read again too.
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["settings"] }),
      queryClient.invalidateQueries({ queryKey: contextQueryKey }),
    ]);
  };

  const reset = async () => {
    setBusy(true);
    setFailure(undefined);
    try {
      await resetSetting(setting.key, place);
      setResetting(false);
      await refresh();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
      setResetting(false);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap={6} component="article" aria-labelledby={titleId} data-setting={setting.key}>
      <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
        <Stack gap={2} style={{ flex: 1, minWidth: 240 }}>
          <Text fw={600} id={titleId}>
            {name}
          </Text>
          <Code w="fit-content">{setting.key}</Code>
          <Text size="sm" c="dimmed" maw={640}>
            {setting.description}
          </Text>
        </Stack>
        <Group gap="xs" wrap="nowrap">
          {canManage && (
            <Button
              variant="default"
              size="xs"
              leftSection={<IconPencil size={14} aria-hidden="true" />}
              onClick={() => setEditing(true)}
              aria-label={t("settings.change.label", { name })}
            >
              {t("settings.change.open")}
            </Button>
          )}
          {canManage && setting.set_here && (
            <Button
              variant="default"
              size="xs"
              leftSection={<IconRestore size={14} aria-hidden="true" />}
              onClick={() => setResetting(true)}
              aria-label={t("settings.reset.label", { name })}
            >
              {t("settings.reset.open")}
            </Button>
          )}
          <Button
            variant="subtle"
            size="xs"
            leftSection={<IconHistory size={14} aria-hidden="true" />}
            onClick={() => setHistory(true)}
            aria-label={t("settings.history.label", { name })}
          >
            {t("settings.history.open")}
          </Button>
        </Group>
      </Group>
      <Group gap="xl" wrap="wrap">
        <Text size="sm">
          <Text span fw={600}>
            {t("settings.valueHere")}
          </Text>{" "}
          <span data-value>{displayValue(setting, setting.value, t)}</span>
        </Text>
        <Text size="sm" c="dimmed">
          {t("settings.default", { value: displayValue(setting, setting.default, t) })}
        </Text>
      </Group>
      <Text size="sm" data-source>
        {sourceSentence(setting, t)}
      </Text>
      {failure !== undefined && (
        <Alert color="danger" role="alert">
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      )}
      {/* Mounted while closed too, so a dialog that closes hands focus back to the button that opened it. */}
      <EditSettingDialog
        opened={editing}
        setting={setting}
        place={place}
        placeLabel={placeLabel}
        name={name}
        onClose={() => setEditing(false)}
        onSaved={refresh}
      />
      <ConfirmDialog
        opened={resetting}
        title={t("settings.reset.title", { name })}
        message={t("settings.reset.message")}
        confirmLabel={t("settings.reset.confirm")}
        cancelLabel={t("settings.cancel")}
        busy={busy}
        onConfirm={() => void reset()}
        onCancel={() => setResetting(false)}
      />
      <SettingHistory opened={history} setting={setting} place={place} name={name} onClose={() => setHistory(false)} />
    </Stack>
  );
}

interface EditValues extends Record<string, unknown> {
  value: unknown;
}

const isJson = (text: string) => {
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
};

/** The form's value for the setting's type: JSON as text, everything else as it is. */
const initialValue = (setting: SettingView): unknown =>
  setting.type === "json"
    ? JSON.stringify(setting.value, null, 2)
    : (setting.value ?? (setting.type === "boolean" ? false : ""));

function editSchema(setting: SettingView, t: TFunction): z.ZodType<EditValues> {
  // The type only: the rule is the server's, and its words come back on the field (TF-057 AC2).
  switch (setting.type) {
    case "number":
      return z.object({ value: z.number({ error: t("settings.edit.number") }) });
    case "boolean":
      return z.object({ value: z.boolean() });
    case "json":
      return z.object({ value: z.string().refine(isJson, t("settings.edit.jsonInvalid")) });
    default:
      return z.object({ value: z.string().trim().min(1, t("settings.edit.required")) });
  }
}

interface EditProps {
  setting: SettingView;
  place: Place;
  placeLabel: string;
  name: string;
  onClose: () => void;
  onSaved: () => Promise<void>;
}

function EditSettingDialog({ opened, ...props }: EditProps & { opened: boolean }) {
  const { t } = useTranslation();
  // The form lives inside the dialog's content, which mounts on every opening: it starts from the current value each time.
  return (
    <FormDialog opened={opened} title={t("settings.edit.title", { name: props.name })} onClose={props.onClose}>
      <EditSettingForm {...props} />
    </FormDialog>
  );
}

function EditSettingForm({ setting, place, placeLabel, onClose, onSaved }: EditProps) {
  const { t } = useTranslation();
  const schema = useMemo(() => editSchema(setting, t), [setting, t]);
  const form = useZodForm<EditValues>({ schema, initialValues: { value: initialValue(setting) } });

  const field = (() => {
    switch (setting.type) {
      case "number":
        return (
          <NumberInput
            label={t("settings.edit.value")}
            description={setting.requirement}
            allowDecimal={!setting.whole_number}
            // No clamping: a value outside the range is sent, and the server's rule comes back on the field in words.
            clampBehavior="none"
            {...(setting.minimum !== null ? { min: setting.minimum } : {})}
            {...(setting.maximum !== null ? { max: setting.maximum } : {})}
            data-autofocus
            {...form.getInputProps("value")}
          />
        );
      case "boolean":
        return (
          <Switch
            label={t("settings.edit.on")}
            description={setting.requirement}
            {...form.getInputProps("value", { type: "checkbox" })}
          />
        );
      case "choice":
        return (
          <Select
            label={t("settings.edit.value")}
            description={setting.requirement}
            allowDeselect={false}
            data={(setting.choices ?? []).map((choice) => ({
              value: choice,
              label: t(`settings.choices.${slug(setting.key)}.${choice}`, { defaultValue: choice }),
            }))}
            {...form.getInputProps("value")}
          />
        );
      case "json":
        return (
          <Textarea
            label={t("settings.edit.value")}
            description={setting.requirement}
            autosize
            minRows={4}
            styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }}
            {...form.getInputProps("value")}
          />
        );
      default:
        return (
          <TextInput
            label={t("settings.edit.value")}
            description={setting.requirement}
            data-autofocus
            {...form.getInputProps("value")}
          />
        );
    }
  })();

  return (
    <Form
      form={form}
      submitLabel={t("settings.edit.save")}
      secondary={{ label: t("settings.cancel"), onClick: onClose }}
      onSubmit={async (values) => {
        const value = setting.type === "json" ? (JSON.parse(String(values.value)) as unknown) : values.value;
        await saveSetting(setting.key, place, typeof value === "string" ? value.trim() : value);
        await onSaved();
        onClose();
      }}
    >
      <Text size="sm">{t("settings.edit.where", { where: placeLabel })}</Text>
      {field}
    </Form>
  );
}

function SettingHistory({
  opened,
  setting,
  place,
  name,
  onClose,
}: {
  opened: boolean;
  setting: SettingView;
  place: Place;
  name: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const history = useQuery({
    queryKey: ["settings", "history", setting.key, place.scope, place.scope === "unit" ? place.unitId : null],
    queryFn: ({ signal }) => fetchSettingHistory(setting.key, place, signal),
    enabled: opened,
  });
  const events = (history.data?.data ?? []).map((change) => ({
    id: change.id,
    at: change.changed_at,
    actor: change.changed_by_name ?? t("settings.source.someone"),
    action:
      change.kind === "reset"
        ? t("settings.history.reset", { before: displayValue(setting, change.before, t) })
        : change.before === null || change.before === undefined
          ? t("settings.history.set", { after: displayValue(setting, change.after, t) })
          : t("settings.history.changed", {
              before: displayValue(setting, change.before, t),
              after: displayValue(setting, change.after, t),
            }),
  }));
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t("settings.history.title", { name })}
      centered
      size="lg"
      closeButtonProps={{ "aria-label": t("dialog.close") }}
    >
      {history.isPending ? (
        <LoadingState height={120} />
      ) : history.isError ? (
        <Alert color="danger" role="alert">
          {t("settings.history.failed")}
        </Alert>
      ) : (
        <HistoryPanel events={events} />
      )}
    </Modal>
  );
}
