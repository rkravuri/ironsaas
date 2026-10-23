import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActionIcon,
  Alert,
  Button,
  Checkbox,
  CopyButton,
  Group,
  List,
  PasswordInput,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  TagsInput,
  Text,
  TextInput,
  ThemeIcon,
} from "@mantine/core";
import type { UseFormReturnType } from "@mantine/form";
import {
  IconAlertTriangle,
  IconCheck,
  IconCopy,
  IconPlugConnected,
  IconPlus,
  IconTrash,
  IconX,
} from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import { fetchRoles, fetchUnits, type Unit } from "@/api/authorization";
import {
  createSsoProvider,
  deleteSsoProvider,
  fetchSsoProvider,
  ssoProvidersQueryKey,
  testSsoProvider,
  updateSsoProvider,
  type SsoProvider,
  type SsoProviderInput,
  type SsoTestItem,
} from "@/api/identity";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { ConsolePage, type ConsoleSection } from "@/components/templates";
import { pathLabel } from "./unit-tree";

const presets = ["entra_id", "google_workspace", "okta", "other"] as const;
type Preset = (typeof presets)[number];

/** Where each preset's discovery document is: the person fills only what differs between tenants (ADR-0048). */
function presetUrl(preset: Preset, detail: string): string | undefined {
  const value = detail.trim();
  switch (preset) {
    case "entra_id":
      return `https://login.microsoftonline.com/${value || "{directory-id}"}/v2.0/.well-known/openid-configuration`;
    case "google_workspace":
      return "https://accounts.google.com/.well-known/openid-configuration";
    case "okta":
      return `https://${value || "{your-domain}.okta.com"}/.well-known/openid-configuration`;
    default:
      return undefined;
  }
}

interface MappingValues {
  group: string;
  roleCode: string;
  unitId: string;
  includeChildren: boolean;
}

interface ProviderValues extends Record<string, unknown> {
  preset: Preset;
  presetDetail: string;
  name: string;
  discoveryUrl: string;
  clientId: string;
  clientSecret: string;
  allowedDomains: string[];
  emailClaim: string;
  nameClaim: string;
  groupsClaim: string;
  justInTime: boolean;
  defaultRoleCode: string;
  defaultHomeUnitId: string;
  ssoOnly: boolean;
  trustProviderMfa: boolean;
  roleMappings: MappingValues[];
}

/** The whole tenant in a unit select: an empty value. */
const wholeTenant = "";

const providerQueryKey = (id: string) => [...ssoProvidersQueryKey, id] as const;

/**
 * One company login (TF-014, ADR-0048), new or existing, on the console template: how to reach the provider, whom it
 * signs in, what happens to the people it creates, and which of its groups map to roles. "Test connection" reads the
 * discovery document and the keys and says in words what works. The client secret is written, never read back: on a
 * change, an empty field keeps the one in the store. Saving, like removing, asks for a recent sign-in.
 */
export function SsoProviderPage() {
  const { t } = useTranslation();
  const { id } = useParams();
  const provider = useQuery({
    queryKey: providerQueryKey(id ?? "new"),
    queryFn: ({ signal }) => fetchSsoProvider(id as string, signal),
    enabled: id !== undefined,
  });
  const roles = useQuery({
    queryKey: ["authz", "roles", "for-people"],
    queryFn: ({ signal }) => fetchRoles("page_size=200&sort=name", signal),
    staleTime: 60_000,
  });
  const units = useQuery({
    queryKey: ["authz", "units"],
    queryFn: ({ signal }) => fetchUnits(signal),
    staleTime: 60_000,
  });

  if ((id !== undefined && provider.isPending) || roles.isPending || units.isPending) {
    return <LoadingState />;
  }
  if (provider.error instanceof ApiError && provider.error.status === 404) {
    return (
      <EmptyState
        drawing="nothingFound"
        title={t("sso.editor.notFoundTitle")}
        text={t("sso.editor.notFoundText")}
        action={
          <Button component={Link} to="/console/sso" variant="default">
            {t("sso.editor.backToList")}
          </Button>
        }
      />
    );
  }
  const error = provider.error ?? roles.error ?? units.error;
  if (error) {
    return (
      <Alert color="danger" role="alert" title={t("sso.editor.failed")}>
        {error instanceof ApiError ? error.message : t("errors.unexpected")}
        {error instanceof ApiError && error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  const roleOptions = (roles.data?.data ?? [])
    .filter((role) => role.principal_type !== "service")
    .map((role) => ({ value: role.code, label: role.name }));
  return (
    <ProviderEditor
      key={provider.data?.id ?? "new"}
      provider={provider.data}
      roleOptions={roleOptions}
      units={units.data?.data ?? []}
    />
  );
}

function ProviderEditor({
  provider,
  roleOptions,
  units,
}: {
  provider: SsoProvider | undefined;
  roleOptions: { value: string; label: string }[];
  units: readonly Unit[];
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const isNew = provider === undefined;
  const [saved, setSaved] = useState(false);
  const [testing, setTesting] = useState(false);
  const [report, setReport] = useState<{ passed: boolean; items: SsoTestItem[] } | undefined>();
  const [testFailure, setTestFailure] = useState<string | undefined>();
  const [removing, setRemoving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [removeFailure, setRemoveFailure] = useState<ApiError | undefined>();
  const redirectUri = `https://${window.location.hostname}/sso/callback`;

  const schema = useMemo(
    () =>
      z.object({
        preset: z.enum(presets),
        presetDetail: z.string(),
        name: z.string().trim().min(1, t("sso.form.nameRequired")).max(100, t("sso.form.nameTooLong")),
        discoveryUrl: z
          .string()
          .trim()
          .url(t("sso.form.discoveryShape"))
          .refine((value) => value.endsWith("/.well-known/openid-configuration"), t("sso.form.discoveryShape")),
        clientId: z.string().trim().min(1, t("sso.form.clientIdRequired")).max(200),
        clientSecret: isNew
          ? z.string().trim().min(1, t("sso.form.clientSecretRequired")).max(1000)
          : z.string().max(1000),
        allowedDomains: z.array(z.string()).min(1, t("sso.form.domainsRequired")).max(50),
        emailClaim: z.string().trim().min(1, t("sso.form.claimRequired")),
        nameClaim: z.string().trim().min(1, t("sso.form.claimRequired")),
        groupsClaim: z.string().trim().min(1, t("sso.form.claimRequired")),
        justInTime: z.boolean(),
        defaultRoleCode: z.string(),
        defaultHomeUnitId: z.string(),
        ssoOnly: z.boolean(),
        trustProviderMfa: z.boolean(),
        roleMappings: z.array(
          z.object({
            group: z.string().trim().min(1, t("sso.form.groupRequired")).max(200),
            roleCode: z.string().min(1, t("sso.form.roleRequired")),
            unitId: z.string(),
            includeChildren: z.boolean(),
          }),
        ),
      }),
    [isNew, t],
  );
  const form = useZodForm<ProviderValues>({
    schema: schema,
    initialValues: {
      preset: (presets as readonly string[]).includes(provider?.preset ?? "") ? (provider!.preset as Preset) : "other",
      presetDetail: "",
      name: provider?.name ?? "",
      discoveryUrl: provider?.discovery_url ?? "",
      clientId: provider?.client_id ?? "",
      clientSecret: "",
      allowedDomains: provider?.allowed_domains ?? [],
      emailClaim: provider?.email_claim ?? "email",
      nameClaim: provider?.name_claim ?? "name",
      groupsClaim: provider?.groups_claim ?? "groups",
      justInTime: provider?.just_in_time ?? false,
      defaultRoleCode: provider?.default_role_code ?? "",
      defaultHomeUnitId: provider?.default_home_unit_id ?? wholeTenant,
      ssoOnly: provider?.sso_only ?? false,
      trustProviderMfa: provider?.trust_provider_mfa ?? true,
      roleMappings: (provider?.role_mappings ?? []).map((mapping) => ({
        group: mapping.group,
        roleCode: mapping.role_code,
        unitId: mapping.unit_id ?? wholeTenant,
        includeChildren: mapping.include_children,
      })),
    },
  });

  const choosePreset = (preset: Preset, detail: string) => {
    form.setFieldValue("preset", preset);
    form.setFieldValue("presetDetail", detail);
    const url = presetUrl(preset, detail);
    if (url !== undefined) {
      form.setFieldValue("discoveryUrl", url);
    }
  };

  const save = async (values: ProviderValues) => {
    setSaved(false);
    const input: SsoProviderInput = {
      name: values.name.trim(),
      preset: values.preset,
      discovery_url: values.discoveryUrl.trim(),
      client_id: values.clientId.trim(),
      client_secret: values.clientSecret.trim() === "" ? null : values.clientSecret.trim(),
      allowed_domains: values.allowedDomains.map((domain) => domain.trim()).filter((domain) => domain !== ""),
      email_claim: values.emailClaim.trim(),
      name_claim: values.nameClaim.trim(),
      groups_claim: values.groupsClaim.trim(),
      just_in_time: values.justInTime,
      default_role_code: values.defaultRoleCode === "" ? null : values.defaultRoleCode,
      default_home_unit_id: values.defaultHomeUnitId === wholeTenant ? null : values.defaultHomeUnitId,
      sso_only: values.ssoOnly,
      trust_provider_mfa: values.trustProviderMfa,
      role_mappings: values.roleMappings.map((mapping) => ({
        group: mapping.group.trim(),
        role_code: mapping.roleCode,
        unit_id: mapping.unitId === wholeTenant ? null : mapping.unitId,
        include_children: mapping.unitId !== wholeTenant && mapping.includeChildren,
      })),
    };
    if (provider === undefined) {
      const created = await createSsoProvider(input);
      await queryClient.invalidateQueries({ queryKey: ssoProvidersQueryKey });
      await navigate(`/console/sso/${created.id}`, { replace: true });
      return;
    }
    const updated = await updateSsoProvider(provider.id, input);
    queryClient.setQueryData(providerQueryKey(provider.id), updated);
    form.setFieldValue("clientSecret", "");
    setSaved(true);
  };

  const test = async () => {
    setTesting(true);
    setReport(undefined);
    setTestFailure(undefined);
    try {
      setReport(await testSsoProvider(form.getValues().discoveryUrl.trim()));
    } catch (error) {
      setTestFailure(error instanceof ApiError ? error.message : t("errors.unexpected"));
    } finally {
      setTesting(false);
    }
  };

  const remove = async () => {
    if (provider === undefined) {
      return;
    }
    setBusy(true);
    try {
      await deleteSsoProvider(provider.id);
      await queryClient.invalidateQueries({ queryKey: ssoProvidersQueryKey });
      await navigate("/console/sso");
    } catch (error) {
      setRemoving(false);
      // A removal held for a second administrator is no failure: the frame says it waits (ADR-0052).
      setRemoveFailure(error instanceof ApiError && !error.isHeld ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  const unitOptions = [
    { value: wholeTenant, label: t("sso.form.wholeTenant") },
    ...units
      .filter((unit) => unit.status === "active")
      .map((unit) => ({ value: unit.id, label: pathLabel(units, unit.id) })),
  ];
  const homeUnitOptions = [{ value: wholeTenant, label: t("sso.form.rootUnit") }, ...unitOptions.slice(1)];
  const values = form.getValues();
  const presetDetailLabel =
    values.preset === "entra_id"
      ? t("sso.form.entraTenant")
      : values.preset === "okta"
        ? t("sso.form.oktaDomain")
        : undefined;

  const sections: ConsoleSection[] = [
    {
      id: "connection",
      title: t("sso.editor.connection"),
      description: t("sso.editor.connectionText"),
      children: (
        <Stack>
          <Select
            label={t("sso.form.preset")}
            data={presets.map((preset) => ({ value: preset, label: t(`sso.presets.${preset}`) }))}
            value={values.preset}
            onChange={(value) => choosePreset(value ?? "other", values.presetDetail)}
            allowDeselect={false}
          />
          {presetDetailLabel !== undefined && (
            <TextInput
              label={presetDetailLabel}
              description={t(values.preset === "entra_id" ? "sso.form.entraTenantHint" : "sso.form.oktaDomainHint")}
              value={values.presetDetail}
              onChange={(event) => choosePreset(values.preset, event.currentTarget.value)}
            />
          )}
          <TextInput label={t("sso.form.name")} description={t("sso.form.nameHint")} {...form.getInputProps("name")} />
          <TextInput
            label={t("sso.form.discoveryUrl")}
            description={t("sso.form.discoveryUrlHint")}
            {...form.getInputProps("discoveryUrl")}
          />
          <TextInput label={t("sso.form.clientId")} autoComplete="off" {...form.getInputProps("clientId")} />
          <PasswordInput
            label={t("sso.form.clientSecret")}
            description={isNew ? t("sso.form.clientSecretHint") : t("sso.form.clientSecretKeep")}
            autoComplete="new-password"
            {...form.getInputProps("clientSecret")}
          />
          <Stack gap={4}>
            <Text size="sm" fw={500} id="sso-redirect-label">
              {t("sso.form.redirectUri")}
            </Text>
            <Group gap="xs" wrap="nowrap">
              <Text
                size="sm"
                ff="var(--iron-font-mono)"
                style={{ wordBreak: "break-all" }}
                aria-labelledby="sso-redirect-label"
              >
                {redirectUri}
              </Text>
              <CopyButton value={redirectUri}>
                {({ copied, copy }) => (
                  <ActionIcon
                    variant="subtle"
                    onClick={copy}
                    aria-label={copied ? t("sso.form.copied") : t("sso.form.copyRedirect")}
                  >
                    {copied ? <IconCheck size={16} aria-hidden="true" /> : <IconCopy size={16} aria-hidden="true" />}
                  </ActionIcon>
                )}
              </CopyButton>
            </Group>
            <Text size="xs" c="dimmed">
              {t("sso.form.redirectUriHint")}
            </Text>
          </Stack>
          <Group>
            <Button
              variant="default"
              leftSection={<IconPlugConnected size={16} aria-hidden="true" />}
              loading={testing}
              onClick={() => void test()}
              disabled={values.discoveryUrl.trim() === ""}
            >
              {t("sso.test.run")}
            </Button>
          </Group>
          {testFailure && (
            <Alert color="danger" role="alert">
              {testFailure}
            </Alert>
          )}
          {report && <TestReport passed={report.passed} items={report.items} />}
        </Stack>
      ),
    },
    {
      id: "who",
      title: t("sso.editor.who"),
      description: t("sso.editor.whoText"),
      children: (
        <Stack>
          <TagsInput
            label={t("sso.form.domains")}
            description={t("sso.form.domainsHint")}
            splitChars={[",", " "]}
            {...form.getInputProps("allowedDomains")}
          />
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <TextInput label={t("sso.form.emailClaim")} {...form.getInputProps("emailClaim")} />
            <TextInput label={t("sso.form.nameClaim")} {...form.getInputProps("nameClaim")} />
            <TextInput label={t("sso.form.groupsClaim")} {...form.getInputProps("groupsClaim")} />
          </SimpleGrid>
          <Switch
            label={t("sso.form.ssoOnly")}
            description={t("sso.form.ssoOnlyHint")}
            {...form.getInputProps("ssoOnly", { type: "checkbox" })}
          />
          <Switch
            label={t("sso.form.trustMfa")}
            description={t("sso.form.trustMfaHint")}
            {...form.getInputProps("trustProviderMfa", { type: "checkbox" })}
          />
        </Stack>
      ),
    },
    {
      id: "new-people",
      title: t("sso.editor.newPeople"),
      description: t("sso.editor.newPeopleText"),
      children: (
        <Stack>
          <Switch
            label={t("sso.form.justInTime")}
            description={t("sso.form.justInTimeHint")}
            {...form.getInputProps("justInTime", { type: "checkbox" })}
          />
          <Select
            label={t("sso.form.defaultRole")}
            description={t("sso.form.defaultRoleHint")}
            data={[{ value: "", label: t("sso.form.noRole") }, ...roleOptions]}
            searchable
            allowDeselect={false}
            {...form.getInputProps("defaultRoleCode")}
          />
          <Select
            label={t("sso.form.defaultHomeUnit")}
            data={homeUnitOptions}
            searchable
            allowDeselect={false}
            {...form.getInputProps("defaultHomeUnitId")}
          />
        </Stack>
      ),
    },
    {
      id: "groups",
      title: t("sso.editor.groups"),
      description: t("sso.editor.groupsText"),
      children: <Mappings form={form} roleOptions={roleOptions} unitOptions={unitOptions} />,
    },
    ...(provider !== undefined
      ? [
          {
            id: "remove",
            title: t("sso.editor.remove"),
            description: t("sso.editor.removeText"),
            children: (
              <Stack align="flex-start">
                {removeFailure && (
                  <Alert color="danger" role="alert" w="100%">
                    {removeFailure.message}
                  </Alert>
                )}
                <Button color="danger" variant="light" onClick={() => setRemoving(true)}>
                  {t("sso.remove.open")}
                </Button>
              </Stack>
            ),
          },
        ]
      : []),
  ];

  return (
    <>
      <Form
        form={form}
        onSubmit={save}
        submitLabel={isNew ? t("sso.form.create") : t("sso.form.save")}
        secondary={{ label: t("dialog.cancel"), onClick: () => void navigate("/console/sso") }}
      >
        <ConsolePage
          title={provider?.name ?? t("sso.editor.newTitle")}
          description={t("sso.editor.description")}
          sections={sections}
        />
        {saved && (
          <Text size="sm" role="status">
            {t("sso.form.saved")}
          </Text>
        )}
      </Form>
      {provider !== undefined && (
        <ConfirmDialog
          opened={removing}
          title={t("sso.remove.title", { name: provider.name })}
          message={t("sso.remove.message", { name: provider.name })}
          confirmLabel={t("sso.remove.confirm")}
          danger
          busy={busy}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </>
  );
}

/** "Test connection"'s answer: each check with its word and its icon, never the colour alone. */
function TestReport({ passed, items }: { passed: boolean; items: readonly SsoTestItem[] }) {
  const { t } = useTranslation();
  const tone = { passed: "success", warning: "warning", failed: "danger" } as const;
  const icon = { passed: IconCheck, warning: IconAlertTriangle, failed: IconX } as const;
  return (
    <Stack gap="xs" data-sso-test-report role="status">
      <Text size="sm" fw={600}>
        {passed ? t("sso.test.passed") : t("sso.test.failed")}
      </Text>
      <List spacing="xs" listStyleType="none" pl={0}>
        {items.map((item) => {
          const Icon = icon[item.status];
          return (
            <List.Item
              key={item.check}
              icon={
                <ThemeIcon color={tone[item.status]} variant="light" size={22} radius="xl">
                  <Icon size={14} aria-hidden="true" />
                </ThemeIcon>
              }
            >
              <Text size="sm" span fw={600}>
                {t(`sso.test.status.${item.status}`)}:{" "}
                {t(`sso.test.checks.${item.check}`, { defaultValue: item.check })}.
              </Text>{" "}
              <Text size="sm" span>
                {item.message}
              </Text>
            </List.Item>
          );
        })}
      </List>
    </Stack>
  );
}

/** The groups that map to roles: a row each, with the role and where it applies. */
function Mappings({
  form,
  roleOptions,
  unitOptions,
}: {
  form: UseFormReturnType<ProviderValues>;
  roleOptions: { value: string; label: string }[];
  unitOptions: { value: string; label: string }[];
}) {
  const { t } = useTranslation();
  const mappings = form.getValues().roleMappings;
  const refusal = form.errors.roleMappings;
  return (
    <Stack>
      {refusal !== undefined && refusal !== null && (
        <Alert color="danger" role="alert" data-mappings-refused>
          {refusal}
        </Alert>
      )}
      {mappings.length === 0 && (
        <Text size="sm" c="dimmed">
          {t("sso.mappings.none")}
        </Text>
      )}
      {mappings.map((mapping, index) => (
        <Stack key={index} gap="xs" role="group" aria-label={t("sso.mappings.row", { number: index + 1 })}>
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <TextInput label={t("sso.mappings.group")} {...form.getInputProps(`roleMappings.${index}.group`)} />
            <Select
              label={t("sso.mappings.role")}
              data={roleOptions}
              searchable
              {...form.getInputProps(`roleMappings.${index}.roleCode`)}
            />
            <Select
              label={t("sso.mappings.where")}
              data={unitOptions}
              searchable
              allowDeselect={false}
              {...form.getInputProps(`roleMappings.${index}.unitId`)}
            />
          </SimpleGrid>
          <Group justify="space-between">
            <Checkbox
              label={t("sso.mappings.includeChildren")}
              disabled={mapping.unitId === wholeTenant}
              {...form.getInputProps(`roleMappings.${index}.includeChildren`, { type: "checkbox" })}
            />
            <Button
              variant="subtle"
              color="danger"
              size="compact-sm"
              leftSection={<IconTrash size={14} aria-hidden="true" />}
              onClick={() => form.removeListItem("roleMappings", index)}
            >
              {t("sso.mappings.remove", { number: index + 1 })}
            </Button>
          </Group>
        </Stack>
      ))}
      <Group>
        <Button
          variant="default"
          leftSection={<IconPlus size={16} aria-hidden="true" />}
          onClick={() =>
            form.insertListItem("roleMappings", {
              group: "",
              roleCode: "",
              unitId: wholeTenant,
              includeChildren: false,
            })
          }
        >
          {t("sso.mappings.add")}
        </Button>
      </Group>
    </Stack>
  );
}
