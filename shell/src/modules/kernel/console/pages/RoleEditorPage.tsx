import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Checkbox,
  Code,
  Fieldset,
  Group,
  Radio,
  Stack,
  Table,
  Text,
  TextInput,
  Textarea,
} from "@mantine/core";
import type { UseFormReturnType } from "@mantine/form";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  createRole,
  deleteRole,
  fetchAssignments,
  fetchPermissions,
  fetchRole,
  updateRole,
  type Permission,
  type Role,
} from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, LoadingState } from "@/components/panels";
import { RecordPage } from "@/components/templates";
import { RiskLabel } from "./RiskLabel";
import { scopeLabel, unitPathLabel } from "./assignment-scope";

const manage = "authz.roles.manage";
const readAssignments = "authz.assignments.read";
const codePattern = /^[a-z][a-z0-9-]+$/;

interface RoleValues extends Record<string, unknown> {
  code: string;
  name: string;
  description: string;
  principalType: "user" | "service";
  permissions: string[];
}

const roleQueryKey = (id: string) => ["authz", "roles", id] as const;

/** One role on the record template (TF-032): its definition and permissions, and who holds it. A new role when there is no id. */
export function RoleEditorPage() {
  const { t } = useTranslation();
  const { id } = useParams();
  const catalogue = useQuery({
    queryKey: ["authz", "permissions"],
    queryFn: ({ signal }) => fetchPermissions(signal),
    staleTime: 300_000,
  });
  const role = useQuery({
    queryKey: roleQueryKey(id ?? "new"),
    queryFn: ({ signal }) => fetchRole(id as string, signal),
    enabled: id !== undefined,
  });

  if (catalogue.isPending || (id !== undefined && role.isPending)) {
    return <LoadingState />;
  }
  const error = catalogue.error ?? role.error;
  if (error instanceof ApiError && error.status === 404) {
    return (
      <EmptyState
        drawing="nothingFound"
        title={t("roles.editor.notFoundTitle")}
        text={t("roles.editor.notFoundText")}
        action={
          <Button component={Link} to="/console/roles" variant="default">
            {t("roles.editor.backToList")}
          </Button>
        }
      />
    );
  }
  if (error || !catalogue.data) {
    return (
      <Alert color="danger" role="alert" title={t("roles.editor.failed")}>
        {error instanceof ApiError ? error.message : t("errors.unexpected")}
        {error instanceof ApiError && error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  return <RoleEditor key={role.data?.id ?? "new"} role={role.data} catalogue={catalogue.data.data} />;
}

function RoleEditor({ role, catalogue }: { role: Role | undefined; catalogue: readonly Permission[] }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const queryClient = useQueryClient();
  const isNew = role === undefined;
  const editable = !(role?.is_system ?? false) && session.tenantPermissions.includes(manage);
  const [saved, setSaved] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();

  const schema = useMemo(
    () =>
      z.object({
        code: isNew
          ? z
              .string()
              .trim()
              .min(2, t("roles.form.codeRequired"))
              .max(100, t("roles.form.codeTooLong"))
              .regex(codePattern, t("roles.form.codeShape"))
          : z.string(),
        name: z.string().trim().min(1, t("roles.form.nameRequired")).max(200, t("roles.form.nameTooLong")),
        description: z.string().max(500, t("roles.form.descriptionTooLong")),
        principalType: z.enum(["user", "service"]),
        permissions: z.array(z.string()),
      }),
    [isNew, t],
  );
  const form = useZodForm<RoleValues>({
    schema,
    initialValues: {
      code: "",
      name: role?.name ?? "",
      description: role?.description ?? "",
      principalType: role?.principal_type === "service" ? "service" : "user",
      permissions: role?.permissions ?? [],
    },
  });

  const save = async (values: RoleValues) => {
    setSaved(false);
    const input = {
      name: values.name.trim(),
      description: values.description.trim(),
      principal_type: values.principalType,
      permissions: values.permissions,
    };
    if (role === undefined) {
      const created = await createRole(values.code.trim(), input);
      await queryClient.invalidateQueries({ queryKey: ["authz", "roles"] });
      await navigate(`/console/roles/${created.id}`, { replace: true });
      return;
    }
    const updated = await updateRole(role.id, input);
    queryClient.setQueryData(roleQueryKey(role.id), updated);
    setSaved(true);
  };

  const remove = async () => {
    if (role === undefined) {
      return;
    }
    setBusy(true);
    try {
      await deleteRole(role.id);
      await queryClient.invalidateQueries({ queryKey: ["authz", "roles"] });
      await navigate("/console/roles");
    } catch (error) {
      setDeleting(false);
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
    }
  };

  const definition = (
    <Stack gap="md">
      {role?.is_system === true && (
        <Alert color="info" title={t("roles.editor.systemTitle")}>
          {t("roles.editor.systemNote")}
        </Alert>
      )}
      {role?.is_system === false && !editable && <Text size="sm">{t("roles.editor.readOnlyNote")}</Text>}
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
      {editable ? (
        <Form
          form={form}
          onSubmit={save}
          submitLabel={isNew ? t("roles.form.create") : t("roles.form.save")}
          secondary={{ label: t("dialog.cancel"), onClick: () => void navigate("/console/roles") }}
        >
          <RoleFields form={form} catalogue={catalogue} isNew={isNew} disabled={false} />
          {saved && (
            <Text size="sm" role="status">
              {t("roles.form.saved")}
            </Text>
          )}
        </Form>
      ) : (
        <RoleFields form={form} catalogue={catalogue} isNew={false} disabled />
      )}
    </Stack>
  );

  const canSeeHolders = role !== undefined && session.permissions.includes(readAssignments);
  return (
    <>
      <RecordPage
        header={{
          title: role?.name ?? t("roles.editor.newTitle"),
          ...(role !== undefined ? { code: role.code } : {}),
          ...(role !== undefined
            ? {
                status: role.is_system
                  ? { label: t("roles.kinds.system"), tone: "neutral" as const }
                  : { label: t("roles.kinds.tenant"), tone: "info" as const },
              }
            : {}),
          ...(role !== undefined
            ? {
                meta: [
                  { label: t("roles.principalType"), value: t(`roles.principalTypes.${role.principal_type}`) },
                  {
                    label: t("roles.permissions"),
                    value: t("roles.permissionCount", { count: role.permissions.length }),
                  },
                ],
              }
            : {}),
          ...(role !== undefined && editable
            ? {
                actions: (
                  <Button variant="default" onClick={() => setDeleting(true)}>
                    {t("roles.delete.open")}
                  </Button>
                ),
              }
            : {}),
        }}
        tabs={[
          { value: "definition", label: t("roles.editor.definition"), panel: definition },
          ...(canSeeHolders
            ? [{ value: "holders", label: t("roles.editor.holders"), panel: <RoleHolders roleId={role.id} /> }]
            : []),
        ]}
      />
      {role !== undefined && (
        <ConfirmDialog
          opened={deleting}
          title={t("roles.delete.title", { name: role.name })}
          message={t("roles.delete.message", { name: role.name })}
          confirmLabel={t("roles.delete.confirm")}
          danger
          busy={busy}
          onConfirm={() => void remove()}
          onCancel={() => setDeleting(false)}
        />
      )}
    </>
  );
}

/** The role's fields; the permissions grouped by module, each with its description, its name and its risk. */
function RoleFields({
  form,
  catalogue,
  isNew,
  disabled,
}: {
  form: UseFormReturnType<RoleValues>;
  catalogue: readonly Permission[];
  isNew: boolean;
  disabled: boolean;
}) {
  const { t } = useTranslation();
  const modules = useMemo(() => {
    const byModule = new Map<string, Permission[]>();
    for (const permission of catalogue) {
      byModule.set(permission.module, [...(byModule.get(permission.module) ?? []), permission]);
    }
    return [...byModule.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [catalogue]);
  const chosen = form.getValues().permissions;
  const toggle = (name: string, on: boolean) => {
    const current = form.getValues().permissions;
    form.setFieldValue(
      "permissions",
      on ? [...current, name].sort() : current.filter((candidate) => candidate !== name),
    );
  };

  return (
    <Stack gap="md">
      {isNew && (
        <TextInput
          label={t("roles.code")}
          description={t("roles.form.codeHint")}
          data-autofocus
          {...form.getInputProps("code")}
        />
      )}
      <TextInput
        label={t("roles.name")}
        disabled={disabled}
        {...(isNew ? {} : { "data-autofocus": true })}
        {...form.getInputProps("name")}
      />
      <Textarea
        label={t("roles.descriptionLabel")}
        autosize
        minRows={2}
        disabled={disabled}
        {...form.getInputProps("description")}
      />
      <Radio.Group
        label={t("roles.principalType")}
        description={t("roles.form.principalTypeHint")}
        {...form.getInputProps("principalType")}
      >
        <Group mt={4}>
          <Radio value="user" label={t("roles.principalTypes.user")} disabled={disabled} />
          <Radio value="service" label={t("roles.principalTypes.service")} disabled={disabled} />
        </Group>
      </Radio.Group>
      <Text size="sm" c="dimmed">
        {t("roles.form.permissionsHint")}
      </Text>
      {modules.map(([module, permissions]) => (
        <Fieldset key={module} legend={t(`roles.modules.${module}`, { defaultValue: module })} variant="filled">
          <Stack gap="sm">
            {permissions.map((permission) => (
              <Checkbox
                key={permission.name}
                label={permission.description}
                description={
                  <Group gap="sm" component="span" wrap="wrap">
                    <Code>{permission.name}</Code>
                    <RiskLabel risk={permission.risk} />
                  </Group>
                }
                checked={chosen.includes(permission.name)}
                disabled={disabled}
                onChange={(event) => toggle(permission.name, event.currentTarget.checked)}
              />
            ))}
          </Stack>
        </Fieldset>
      ))}
    </Stack>
  );
}

/** Who holds the role and where, for the readers of assignments (ADR-0031: each row as far as the reader may see). */
function RoleHolders({ roleId }: { roleId: string }) {
  const { t } = useTranslation();
  const holders = useQuery({
    queryKey: ["authz", "role-assignments", "role", roleId],
    queryFn: ({ signal }) => fetchAssignments(`filter=role_id:eq:${roleId}&page_size=100`, signal),
  });
  if (holders.isPending) {
    return <LoadingState height={120} />;
  }
  if (holders.isError) {
    return (
      <Alert color="danger" role="alert">
        {holders.error instanceof ApiError ? holders.error.message : t("errors.unexpected")}
      </Alert>
    );
  }
  if (holders.data.data.length === 0) {
    return <EmptyState drawing="nothingYet" size="sm" title={t("roles.holders.none")} />;
  }
  return (
    <Table withTableBorder>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t("roles.holders.who")}</Table.Th>
          <Table.Th>{t("roles.holders.where")}</Table.Th>
          <Table.Th>{t("roles.holders.since")}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {holders.data.data.map((assignment) => (
          <Table.Tr key={assignment.id}>
            <Table.Td>{assignment.principal_name ?? assignment.principal_id}</Table.Td>
            <Table.Td>
              {scopeLabel(assignment, t)}
              {assignment.unit_path.length > 0 && (
                <Text size="xs" c="dimmed">
                  {unitPathLabel(assignment)}
                </Text>
              )}
            </Table.Td>
            <Table.Td>{new Date(assignment.created_at).toLocaleDateString()}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}
