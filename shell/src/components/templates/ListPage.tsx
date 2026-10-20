import type { ReactNode } from "react";
import { Box, Group, Stack } from "@mantine/core";
import { DensitySwitch } from "../DensitySwitch";
import { PageOpener } from "./PageOpener";

export interface ListPageProps {
  title: string;
  description?: string;
  drawing?: ReactNode;
  actions?: ReactNode;
  /** The filters bar between the opener and the grid. */
  filters?: ReactNode;
  /** The density switch belongs to a list of rows; off when the body is not a grid. */
  density?: boolean;
  /** The grid, with its own empty state. */
  children: ReactNode;
}

/** A list: the opener, the filters, then the grid filling the rest of the viewport (TF-170, brief section 2). */
export function ListPage({ title, description, drawing, actions, filters, density = true, children }: ListPageProps) {
  const opener = {
    title,
    ...(description !== undefined ? { description } : {}),
    ...(drawing !== undefined ? { drawing } : {}),
  };
  return (
    <Stack
      gap="md"
      style={{ height: "calc(100dvh - var(--app-shell-header-height) - 2 * var(--app-shell-padding))" }}
      data-template="list"
    >
      <PageOpener
        {...opener}
        actions={
          <>
            {actions}
            {density && <DensitySwitch />}
          </>
        }
      />
      {filters !== undefined && (
        <Group gap="sm" wrap="wrap">
          {filters}
        </Group>
      )}
      <Box style={{ flex: 1, minHeight: 0 }}>{children}</Box>
    </Stack>
  );
}
