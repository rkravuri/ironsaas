import { useEffect, useMemo, useState, type FocusEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Badge, Box, Button, Flex, Group, Paper, Stack, Table, Text, Tree, getTreeExpandedState, useTree } from "@mantine/core";
import { IconChevronRight } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import { deactivateUnit, fetchKinds, fetchUnits, type Kinds, type Unit } from "@/api/authorization";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { ConfirmDialog, EmptyState, LoadingState, RecordHeader } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { ModuleMark } from "@/design/patterns";
import { CreateUnitDialog, MoveUnitDialog, RenameUnitDialog } from "./UnitDialogs";
import { childKinds, kindOf, pathLabel, toTreeData } from "./unit-tree";

const manage = "authz.units.manage";
const unitsQueryKey = ["authz", "organisation-units"] as const;
const kindsQueryKey = ["authz", "organisation-kinds"] as const;

type DialogName = "create" | "rename" | "move" | "deactivate";

interface Failure {
  message: string;
  correlationId: string | undefined;
}

const failureOf = (error: unknown, fallback: string): Failure =>
  error instanceof ApiError ? { message: error.message, correlationId: error.correlationId } : { message: fallback, correlationId: undefined };

/** The organisation tree of the tenant (TF-034): the tree on the left, the chosen unit's record on the right. */
export function UnitsPage() {
  const { t } = useTranslation();
  const units = useQuery({ queryKey: unitsQueryKey, queryFn: ({ signal }) => fetchUnits(signal) });
  const kinds = useQuery({ queryKey: kindsQueryKey, queryFn: ({ signal }) => fetchKinds(signal) });
  const opener = {
    title: t("units.title"),
    description: t("units.description"),
    drawing: <ModuleMark name="console" accent="console" size={56} />,
    density: false,
  };

  if (units.isPending) {
    return (
      <ListPage {...opener}>
        <LoadingState />
      </ListPage>
    );
  }
  if (units.isError) {
    const failure = failureOf(units.error, t("units.failed"));
    return (
      <ListPage {...opener}>
        <Alert color="danger" role="alert" title={t("units.failed")}>
          {failure.message}
          {failure.correlationId !== undefined && (
            <Text size="xs" c="dimmed" mt={4}>
              {t("errors.correlation", { id: failure.correlationId })}
            </Text>
          )}
        </Alert>
      </ListPage>
    );
  }
  if (units.data.data.length === 0) {
    return (
      <ListPage {...opener}>
        <EmptyState drawing="nothingYet" title={t("units.empty.title")} text={t("units.empty.text")} />
      </ListPage>
    );
  }
  return (
    <ListPage {...opener}>
      <UnitsWorkspace units={units.data.data} kinds={kinds.data} />
    </ListPage>
  );
}

/** Mounted once the tree has loaded, so the tree opens fully expanded on its first render. */
function UnitsWorkspace({ units, kinds }: { units: readonly Unit[]; kinds: Kinds | undefined }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const data = useMemo(() => toTreeData(units), [units]);
  const byId = useMemo(() => new Map(units.map((unit) => [unit.id, unit])), [units]);
  // The quick action "Add a unit" (D4) arrives as ?add: the person's home unit is chosen, and its dialog opens once the
  // answers say a unit may be added there.
  const [params, setParams] = useSearchParams();
  const home = useSession().me?.home_unit?.id;
  const [adding, setAdding] = useState(() => params.has("add"));
  const [selectedId, setSelectedId] = useState<string | undefined>();
  const [dialog, setDialog] = useState<DialogName | undefined>();
  useEffect(() => {
    if (params.has("add")) {
      const rest = new URLSearchParams(params);
      rest.delete("add");
      setParams(rest, { replace: true });
    }
  }, [params, setParams]);
  const [failure, setFailure] = useState<Failure | undefined>();
  const [busy, setBusy] = useState(false);
  // Until the person picks a unit, the quick action's home unit is the choice; otherwise the root.
  const preferred = adding && home !== undefined && byId.has(home) ? home : undefined;
  const chosen = selectedId ?? preferred;
  const selected = (chosen !== undefined ? byId.get(chosen) : undefined) ?? units[0];
  // The actions follow what the principal may do at the chosen unit, not tenant-wide (ADR-0031); the API checks again.
  const access = useAccessAt(selected ? { unit: selected.id } : undefined, [manage]);
  const canManage = access.can(manage);
  const tree = useTree({
    initialExpandedState: getTreeExpandedState(data, "*"),
    selectedState: selected ? [selected.id] : [],
    onSelectedStateChange: (state) => setSelectedId(state[0]),
  });

  const canHoldHere = selected !== undefined && selected.status === "active" && childKinds(kinds, selected.kind).length > 0;
  const shown = dialog ?? (adding && canManage && canHoldHere ? "create" : undefined);
  const close = () => {
    setDialog(undefined);
    setAdding(false);
  };
  const done = async (unit: Unit, expand?: string | null) => {
    setDialog(undefined);
    setAdding(false);
    setFailure(undefined);
    await queryClient.invalidateQueries({ queryKey: unitsQueryKey });
    if (expand) {
      tree.expand(expand);
    }
    setSelectedId(unit.id);
  };
  const deactivate = async () => {
    if (!selected) {
      return;
    }
    setBusy(true);
    try {
      await done(await deactivateUnit(selected.id));
    } catch (error) {
      setDialog(undefined);
      setFailure(failureOf(error, t("errors.unexpected")));
    } finally {
      setBusy(false);
    }
  };
  // Selection follows the keyboard: the arrow keys move the focus through the tree, and the record follows it.
  const followFocus = (event: FocusEvent<HTMLUListElement>) => {
    const value = (event.target as HTMLElement).closest("[role=treeitem]")?.getAttribute("data-value");
    if (value) {
      setSelectedId(value);
    }
  };

  return (
    <Flex direction={{ base: "column", sm: "row" }} gap="md" h="100%" align="stretch">
      <Paper withBorder p="xs" w={{ base: "100%", sm: 320 }} style={{ flexShrink: 0, overflow: "auto" }}>
        <Tree
          data={data}
          tree={tree}
          selectOnClick
          expandOnClick={false}
          aria-label={t("units.treeLabel")}
          onFocus={followFocus}
          renderNode={({ node, expanded, hasChildren, elementProps }) => {
            const unit = byId.get(node.value);
            const inactive = unit?.status === "inactive";
            return (
              <Group gap={6} wrap="nowrap" {...elementProps}>
                <Box
                  w={16}
                  c="dimmed"
                  aria-hidden="true"
                  style={{ display: "inline-flex", flexShrink: 0 }}
                  onClick={(event) => {
                    event.stopPropagation();
                    tree.toggleExpanded(node.value);
                  }}
                >
                  {hasChildren && <IconChevronRight size={14} style={{ transform: expanded ? "rotate(90deg)" : "none" }} />}
                </Box>
                <Text size="sm" truncate {...(inactive ? { c: "dimmed" } : {})}>
                  {node.label}
                </Text>
                {inactive && (
                  <Badge size="xs" variant="default">
                    {t("units.statuses.inactive")}
                  </Badge>
                )}
              </Group>
            );
          }}
        />
      </Paper>
      <Paper withBorder p="md" style={{ flex: 1, minWidth: 0, overflow: "auto" }}>
        {selected ? (
          <UnitRecord unit={selected} units={units} kinds={kinds} canManage={canManage} failure={failure} onAction={setDialog} />
        ) : (
          <EmptyState drawing="nothingFound" size="sm" title={t("units.select")} />
        )}
      </Paper>
      {selected && shown === "create" && <CreateUnitDialog unit={selected} kinds={kinds} onClose={close} onDone={(unit) => done(unit, unit.parent_id)} />}
      {selected && dialog === "rename" && <RenameUnitDialog unit={selected} onClose={close} onDone={(unit) => done(unit)} />}
      {selected && dialog === "move" && <MoveUnitDialog unit={selected} units={units} kinds={kinds} onClose={close} onDone={(unit) => done(unit, unit.parent_id)} />}
      {selected && (
        <ConfirmDialog
          opened={dialog === "deactivate"}
          title={t("units.deactivate.title", { name: selected.name })}
          message={t("units.deactivate.message", { name: selected.name })}
          confirmLabel={t("units.deactivate.confirm")}
          danger
          busy={busy}
          onConfirm={() => void deactivate()}
          onCancel={close}
        />
      )}
    </Flex>
  );
}

/** One unit on the record header: its status as a word, its kind, how many units sit below it, and what can be done to it. */
function UnitRecord({
  unit,
  units,
  kinds,
  canManage,
  failure,
  onAction,
}: {
  unit: Unit;
  units: readonly Unit[];
  kinds: Kinds | undefined;
  canManage: boolean;
  failure: Failure | undefined;
  onAction: (dialog: DialogName) => void;
}) {
  const { t } = useTranslation();
  const isRoot = unit.parent_id === null;
  const active = unit.status === "active";
  const below = units.filter((candidate) => candidate.parent_id === unit.id).length;
  const canHoldUnits = active && childKinds(kinds, unit.kind).length > 0;
  const when = (value: string) => new Date(value).toLocaleString();
  return (
    <Stack gap="md">
      <RecordHeader
        title={unit.name}
        code={unit.code}
        status={{ label: t(`units.statuses.${unit.status}`), tone: active ? "success" : "neutral" }}
        meta={[
          { label: t("units.kind"), value: kindOf(kinds, unit.kind)?.name ?? unit.kind },
          { label: t("units.below"), value: String(below) },
        ]}
      />
      {canManage && (
        <Group gap="sm">
          {canHoldUnits && <Button onClick={() => onAction("create")}>{t("units.actions.add")}</Button>}
          <Button variant="default" onClick={() => onAction("rename")}>
            {t("units.actions.rename")}
          </Button>
          {!isRoot && (
            <Button variant="default" onClick={() => onAction("move")}>
              {t("units.actions.move")}
            </Button>
          )}
          {!isRoot && active && (
            <Button variant="default" onClick={() => onAction("deactivate")}>
              {t("units.actions.deactivate")}
            </Button>
          )}
        </Group>
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
      <Table variant="vertical" withTableBorder>
        <Table.Tbody>
          <Table.Tr>
            <Table.Th w={180}>{t("units.path")}</Table.Th>
            <Table.Td>{pathLabel(units, unit.id)}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("units.created")}</Table.Th>
            <Table.Td>{when(unit.created_at)}</Table.Td>
          </Table.Tr>
          <Table.Tr>
            <Table.Th>{t("units.updated")}</Table.Th>
            <Table.Td>{when(unit.updated_at)}</Table.Td>
          </Table.Tr>
        </Table.Tbody>
      </Table>
      {isRoot && (
        <Text size="sm" c="dimmed">
          {t("units.rootNote")}
        </Text>
      )}
    </Stack>
  );
}
