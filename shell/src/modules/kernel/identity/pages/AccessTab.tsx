import { useEffect, useMemo, useState } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Checkbox, Group, Radio, Select, Stack, Table, Text, VisuallyHidden } from "@mantine/core";
import { IconHelpCircle, IconPlus } from "@tabler/icons-react";
import { z } from "zod";
import { ApiError } from "@/api/client";
import {
  fetchAssignments,
  fetchRoles,
  fetchUnits,
  grantRole,
  previewGrantConflicts,
  removeAssignment,
  type Assignment,
  type ScopeKind,
} from "@/api/authorization";
import type { UserRecord } from "@/api/identity";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { ConfirmDialog, EmptyState, FormDialog, LoadingState } from "@/components/panels";
import { scopeLabel, unitPathLabel } from "@/modules/kernel/console/pages/assignment-scope";
import { GrantConflictNotice } from "@/modules/kernel/console/pages/GrantConflictNotice";
import { UnitPicker } from "@/modules/kernel/console/pages/UnitPicker";

const read = "authz.assignments.read";
const manage = "authz.assignments.manage";

const assignmentsKey = (principal: string) => ["authz", "role-assignments", "principal", principal] as const;

/**
 * A user's access (TF-033, ADR-0031): each assignment with its role, where it applies and the unit's path. Granting
 * and removing appear where the reader may manage assignments; the API checks every grant against the granter's own
 * permissions at the scope, so the dialog never offers more than the reader could give.
 */
export function AccessTab({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [granting, setGranting] = useState(false);
  const [removing, setRemoving] = useState<Assignment | undefined>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiError | undefined>();
  const canRead = session.permissions.includes(read);
  const assignments = useQuery({
    queryKey: assignmentsKey(user.id),
    queryFn: ({ signal }) => fetchAssignments(`filter=principal_id:eq:${user.id}&page_size=100`, signal),
    enabled: canRead,
  });

  if (!canRead) {
    return <EmptyState drawing="noAccess" size="sm" title={t("access.noReadTitle")} text={t("access.noRead")} />;
  }

  const refresh = () => queryClient.invalidateQueries({ queryKey: assignmentsKey(user.id) });
  const remove = async () => {
    if (!removing) {
      return;
    }
    setBusy(true);
    setFailure(undefined);
    try {
      await removeAssignment(removing.id);
      await refresh();
    } catch (error) {
      setFailure(error instanceof ApiError ? error : undefined);
    } finally {
      setBusy(false);
      setRemoving(undefined);
    }
  };

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start">
        <Text size="sm" c="dimmed" maw={640}>
          {t("access.intro", { name: user.display_name })}
        </Text>
        <Group gap="sm">
          {session.permissions.includes("authz.access.explain") && (
            <Button
              variant="default"
              leftSection={<IconHelpCircle size={16} aria-hidden="true" />}
              title={t("access.explainHint")}
              onClick={() =>
                void navigate(
                  `/console/explain?principal=${encodeURIComponent(user.id)}&name=${encodeURIComponent(user.display_name)}`,
                )
              }
            >
              {t("access.explain")}
            </Button>
          )}
          {session.permissions.includes(manage) && (
            <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={() => setGranting(true)}>
              {t("access.grant.open")}
            </Button>
          )}
        </Group>
      </Group>
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
      {assignments.isPending ? (
        <LoadingState height={120} />
      ) : assignments.isError ? (
        <Alert color="danger" role="alert">
          {assignments.error instanceof ApiError ? assignments.error.message : t("errors.unexpected")}
        </Alert>
      ) : assignments.data.data.length === 0 ? (
        <EmptyState drawing="nothingYet" size="sm" title={t("access.none")} text={t("access.noneText")} />
      ) : (
        <Table withTableBorder aria-label={t("access.tableLabel", { name: user.display_name })}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t("access.role")}</Table.Th>
              <Table.Th>{t("access.scope")}</Table.Th>
              <Table.Th>{t("access.unit")}</Table.Th>
              <Table.Th>{t("access.since")}</Table.Th>
              <Table.Th>
                <VisuallyHidden>{t("access.actions")}</VisuallyHidden>
              </Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {assignments.data.data.map((assignment) => (
              <AssignmentRow key={assignment.id} assignment={assignment} onRemove={() => setRemoving(assignment)} />
            ))}
          </Table.Tbody>
        </Table>
      )}
      {granting && (
        <GrantRoleDialog
          user={user}
          onClose={() => setGranting(false)}
          onDone={async () => {
            setGranting(false);
            await refresh();
          }}
        />
      )}
      <ConfirmDialog
        opened={removing !== undefined}
        title={t("access.remove.title", { role: removing?.role_name ?? "" })}
        message={t("access.remove.message", {
          role: removing?.role_name ?? "",
          name: user.display_name,
          scope: removing ? where(removing, t) : "",
        })}
        confirmLabel={t("access.remove.confirm")}
        danger
        busy={busy}
        onConfirm={() => void remove()}
        onCancel={() => setRemoving(undefined)}
      />
    </Stack>
  );
}

const where = (assignment: Assignment, t: TFunction) =>
  assignment.unit_path.length > 0
    ? `${t(`access.scopes.${assignment.scope_kind}`)}: ${unitPathLabel(assignment)}`
    : t(`access.scopes.${assignment.scope_kind}`);

/**
 * One assignment. Remove shows only where it will work: the reader manages assignments at its unit, over the unit and
 * every unit below it for a "unit and below" assignment, or for the whole tenant (ADR-0031).
 */
function AssignmentRow({ assignment, onRemove }: { assignment: Assignment; onRemove: () => void }) {
  const { t } = useTranslation();
  const session = useSession();
  const target =
    assignment.unit_id !== null
      ? { unit: assignment.unit_id, includeChildren: assignment.scope_kind === "unit_with_children" }
      : undefined;
  const access = useAccessAt(target, [manage]);
  const canRemove = assignment.unit_id !== null ? access.can(manage) : session.tenantPermissions.includes(manage);
  return (
    <Table.Tr>
      <Table.Td>
        <Text size="sm">{assignment.role_name}</Text>
        <Text size="xs" c="dimmed" ff="monospace">
          {assignment.role_code}
        </Text>
      </Table.Td>
      <Table.Td>{scopeLabel(assignment, t)}</Table.Td>
      <Table.Td>{unitPathLabel(assignment) || t("access.noUnit")}</Table.Td>
      <Table.Td>{new Date(assignment.created_at).toLocaleDateString()}</Table.Td>
      <Table.Td>
        {canRemove && (
          <Button
            size="xs"
            variant="default"
            onClick={onRemove}
            aria-label={t("access.remove.label", { role: assignment.role_name })}
          >
            {t("access.remove.open")}
          </Button>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

interface GrantValues extends Record<string, unknown> {
  roleId: string;
  scope: "tenant" | "unit";
  unitId: string;
  includeChildren: boolean;
}

/** Grant a role: the role, then where (the tenant or a unit on the tree, with or without the units below it). */
function GrantRoleDialog({
  user,
  onClose,
  onDone,
}: {
  user: UserRecord;
  onClose: () => void;
  onDone: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const session = useSession();
  const forTenant = session.tenantPermissions.includes(manage);
  const kind = user.type === "user" ? "user" : "service";
  const roles = useQuery({
    queryKey: ["authz", "roles", "grantable"],
    queryFn: ({ signal }) => fetchRoles("page_size=200&sort=name", signal),
  });
  const canSeeUnits = session.permissions.includes("authz.units.read");
  const units = useQuery({
    queryKey: ["authz", "organisation-units"],
    queryFn: ({ signal }) => fetchUnits(signal),
    enabled: canSeeUnits,
  });
  const unitList = useMemo(() => units.data?.data ?? [], [units.data]);
  const options = (roles.data?.data ?? [])
    .filter((role) => role.principal_type === "any" || role.principal_type === kind)
    .map((role) => ({ value: role.id, label: role.name }));
  const homeVisible = user.home_unit !== null && unitList.some((unit) => unit.id === user.home_unit?.id);

  const schema = useMemo(
    () =>
      z
        .object({
          roleId: z.string().min(1, t("access.grant.roleRequired")),
          scope: z.enum(["tenant", "unit"]),
          unitId: z.string(),
          includeChildren: z.boolean(),
        })
        .superRefine((values, context) => {
          if (values.scope === "unit" && values.unitId === "") {
            context.addIssue({ code: "custom", path: ["unitId"], message: t("access.grant.unitRequired") });
          }
        }),
    [t],
  );
  const form = useZodForm<GrantValues>({
    schema,
    initialValues: {
      roleId: "",
      scope: forTenant && !canSeeUnits ? "tenant" : "unit",
      unitId: homeVisible ? (user.home_unit?.id ?? "") : "",
      includeChildren: true,
    },
  });
  const values = form.getValues();
  // The conflict rules are asked about the grant as it stands, so the dialog says what they will do before the confirm
  // (TF-036): a grant still missing its role or unit is not asked about.
  const question = useMemo(() => {
    if (values.roleId === "" || (values.scope === "unit" && values.unitId === "")) {
      return undefined;
    }
    const scopeKind: ScopeKind =
      values.scope === "tenant" ? "tenant" : values.includeChildren ? "unit_with_children" : "unit";
    return {
      principal_id: user.id,
      role_id: values.roleId,
      scope_kind: scopeKind,
      ...(values.scope === "unit" ? { unit_id: values.unitId } : {}),
    };
  }, [user.id, values.roleId, values.scope, values.unitId, values.includeChildren]);
  const conflicts = useQuery({
    queryKey: ["authz", "grant-conflicts", question],
    queryFn: ({ signal }) => (question ? previewGrantConflicts(question, signal) : Promise.resolve({ data: [] })),
    enabled: question !== undefined,
  });
  // The tree arrives after the dialog opens; the user's home unit is the first choice once it is there.
  const home = homeVisible ? user.home_unit?.id : undefined;
  useEffect(() => {
    if (home !== undefined && form.getValues().unitId === "") {
      form.setFieldValue("unitId", home);
    }
  }, [home, form]);

  const submit = async (chosen: GrantValues) => {
    const scopeKind: ScopeKind =
      chosen.scope === "tenant" ? "tenant" : chosen.includeChildren ? "unit_with_children" : "unit";
    await grantRole({
      principal_id: user.id,
      role_id: chosen.roleId,
      scope_kind: scopeKind,
      ...(chosen.scope === "unit" ? { unit_id: chosen.unitId } : {}),
    });
    await onDone();
  };

  return (
    <FormDialog opened title={t("access.grant.title", { name: user.display_name })} onClose={onClose}>
      {/* The form is there from the start, so the dialog can focus its first field; the lists fill in as they arrive. */}
      <Form
        form={form}
        onSubmit={submit}
        submitLabel={t("access.grant.submit")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Select
          label={t("access.role")}
          description={t("access.grant.roleHint")}
          data={options}
          searchable
          nothingFoundMessage={roles.isPending ? t("access.grant.loadingRoles") : t("access.grant.noRoles")}
          data-autofocus
          {...form.getInputProps("roleId")}
        />
        {forTenant && canSeeUnits && (
          <Radio.Group label={t("access.grant.where")} {...form.getInputProps("scope")}>
            <Group mt={4}>
              <Radio value="unit" label={t("access.grant.atUnit")} />
              <Radio value="tenant" label={t("access.scopes.tenant")} />
            </Group>
          </Radio.Group>
        )}
        {values.scope === "unit" &&
          (!canSeeUnits ? (
            <Text size="sm">{t("access.grant.noUnits")}</Text>
          ) : units.isPending ? (
            <LoadingState height={120} />
          ) : (
            <>
              <UnitPicker
                label={t("access.unit")}
                units={unitList}
                value={values.unitId}
                onChange={(id) => form.setFieldValue("unitId", id)}
                error={typeof form.errors.unitId === "string" ? form.errors.unitId : undefined}
                disabled={(unit) => unit.status !== "active"}
              />
              <Checkbox
                label={t("access.grant.includeChildren")}
                description={t("access.grant.includeChildrenHint")}
                {...form.getInputProps("includeChildren", { type: "checkbox" })}
              />
            </>
          ))}
        {question !== undefined && conflicts.data && (
          <GrantConflictNotice name={user.display_name} conflicts={conflicts.data.data} />
        )}
      </Form>
    </FormDialog>
  );
}
