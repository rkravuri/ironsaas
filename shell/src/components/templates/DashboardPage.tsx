import type { ReactNode } from "react";
import { Paper, SimpleGrid, Stack, Text } from "@mantine/core";
import type { StatusTone } from "../panels/RecordHeader";
import { PageOpener } from "./PageOpener";

export interface DashboardCard {
  id: string;
  label: string;
  /** Already formatted: the page knows its units. */
  value: string;
  hint?: string;
  tone?: StatusTone;
}

export interface DashboardPageProps {
  title: string;
  description?: string;
  cards: DashboardCard[];
  /** Anything below the cards: a grid, a chart. */
  children?: ReactNode;
}

/** A dashboard: cards with a big number in the display family, the moment of the page (brief section 6). */
export function DashboardPage({ title, description, cards, children }: DashboardPageProps) {
  return (
    <Stack gap="lg" data-template="dashboard">
      <PageOpener title={title} {...(description !== undefined ? { description } : {})} />
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="md">
        {cards.map((card) => (
          <Paper key={card.id} withBorder p="lg" component="section" aria-label={card.label}>
            <Stack gap={4}>
              <Text size="sm" c="dimmed">
                {card.label}
              </Text>
              <Text component="p" m={0} style={{ fontFamily: "var(--iron-font-display)", fontSize: "var(--mantine-font-size-xxl)", lineHeight: 1.25 }} {...(card.tone !== undefined && card.tone !== "neutral" ? { c: card.tone } : {})}>
                {card.value}
              </Text>
              {card.hint !== undefined && (
                <Text size="xs" c="dimmed">
                  {card.hint}
                </Text>
              )}
            </Stack>
          </Paper>
        ))}
      </SimpleGrid>
      {children}
    </Stack>
  );
}
