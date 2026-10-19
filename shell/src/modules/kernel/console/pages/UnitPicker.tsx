import { useId, useMemo, type FocusEvent } from "react";
import { Box, Group, Paper, Stack, Text, Tree, getTreeExpandedState, useTree } from "@mantine/core";
import { IconChevronRight } from "@tabler/icons-react";
import type { Unit } from "@/api/authorization";
import { pathLabel, toTreeData } from "./unit-tree";

export interface UnitPickerProps {
  label: string;
  units: readonly Unit[];
  value: string;
  onChange: (id: string) => void;
  error?: string | undefined;
  /** Units that cannot be chosen, such as inactive ones; they stay in the tree so the path reads right. */
  disabled?: (unit: Unit) => boolean;
}

/** A unit chosen on the tree (TF-034): the arrow keys or a click choose, and the path of the choice is written below. */
export function UnitPicker({ label, units, value, onChange, error, disabled }: UnitPickerProps) {
  const id = useId();
  const data = useMemo(() => toTreeData(units), [units]);
  const byId = useMemo(() => new Map(units.map((unit) => [unit.id, unit])), [units]);
  const choose = (candidate: string | undefined) => {
    const unit = candidate !== undefined ? byId.get(candidate) : undefined;
    if (unit && !(disabled?.(unit) ?? false)) {
      onChange(unit.id);
    }
  };
  const tree = useTree({
    initialExpandedState: getTreeExpandedState(data, "*"),
    selectedState: value ? [value] : [],
    onSelectedStateChange: (state) => choose(state[0]),
  });
  const followFocus = (event: FocusEvent<HTMLUListElement>) => {
    const focused = (event.target as HTMLElement).closest("[role=treeitem]")?.getAttribute("data-value");
    if (focused) {
      choose(focused);
    }
  };

  return (
    <Stack gap={4}>
      <Text id={`${id}-label`} size="sm" fw={500}>
        {label}
      </Text>
      <Paper withBorder p="xs" mah={240} style={{ overflow: "auto" }}>
        <Tree
          data={data}
          tree={tree}
          selectOnClick
          expandOnClick={false}
          aria-labelledby={`${id}-label`}
          {...(error !== undefined ? { "aria-describedby": `${id}-error`, "aria-invalid": true } : {})}
          onFocus={followFocus}
          renderNode={({ node, expanded, hasChildren, selected, elementProps }) => {
            const unit = byId.get(node.value);
            const off = unit !== undefined && (disabled?.(unit) ?? false);
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
                <Text size="sm" truncate fw={selected ? 600 : 400} {...(off ? { c: "dimmed" } : {})}>
                  {node.label}
                </Text>
              </Group>
            );
          }}
        />
      </Paper>
      <Text size="xs" c="dimmed" aria-live="polite">
        {value ? pathLabel(units, value) : " "}
      </Text>
      {error !== undefined && (
        <Text id={`${id}-error`} size="xs" c="danger" role="alert">
          {error}
        </Text>
      )}
    </Stack>
  );
}
