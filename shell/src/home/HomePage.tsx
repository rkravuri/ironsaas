import { useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Anchor, Box, Button, Grid, Group, Paper, SimpleGrid, Stack, Text, Title } from "@mantine/core";
import type { Me } from "@/api/identity";
import { useSession } from "@/auth/session-context";
import { Home } from "@/design/illustrations";
import { ModuleMark, TenantMark } from "@/design/patterns";
import { manifests } from "@/modules";
import { openersFor, routeAt, type ModuleOpening } from "@/modules/registry";
import { useQuickActions } from "@/shell/quick-actions";
import { dayPart } from "./greeting";
import { readRecent, type RecentPage } from "./recent";

/** Which session's home page has drawn itself in already: once per session, in this browser. */
const drawnKey = "iron.home.drawn";

function drawnFor(): string | null {
  try {
    return window.localStorage.getItem(drawnKey);
  } catch {
    return null;
  }
}

function markDrawn(session: string): void {
  try {
    window.localStorage.setItem(drawnKey, session);
  } catch {
    // Storage may refuse; the openers then draw in again next time, which is all that happens.
  }
}

/**
 * The home page (ADR-0033), the landing page after sign-in: a moment, so the art leads here. The greeting, the Home
 * drawing and the tenant mark open it; then one opener per module the principal may open, the quick actions it may
 * perform at its home unit, and the pages it opened last. Nothing here polls the API or moves while the person reads;
 * the marks draw in once per session, and not at all with reduced motion.
 */
export function HomePage() {
  const { me } = useSession();
  // Behind the session guard the person is known; the page waits for them rather than drawing for nobody.
  return me ? <HomeView me={me} /> : null;
}

function HomeView({ me }: { me: Me }) {
  const { t } = useTranslation();
  const session = useSession();
  const hostname = window.location.hostname;
  const openers = useMemo(() => openersFor(manifests, session.permissions, { strict: import.meta.env.DEV }), [session.permissions]);
  const sessionId = me.session.id;
  const [drawIn] = useState(() => drawnFor() !== sessionId);
  const [part] = useState(() => dayPart(new Date().getHours()));
  useEffect(() => {
    markDrawn(sessionId);
  }, [sessionId]);

  return (
    <Stack gap="xl" data-page="home">
      <Group justify="space-between" align="center" wrap="nowrap" gap="xl" component="header">
        <Stack gap="xs" maw={640}>
          <Group gap="sm" wrap="nowrap">
            <TenantMark hostname={hostname} size={32} animate={drawIn} />
            <Text c="dimmed" size="sm">
              {t("app.tenant", { hostname })}
            </Text>
          </Group>
          <Title order={1} variant="display">
            {t(`home.greeting.${part}`, { name: me.display_name })}
          </Title>
          <Text c="dimmed">{me.home_unit ? t("home.introWithUnit", { unit: me.home_unit.name }) : t("home.intro")}</Text>
        </Stack>
        {/* The mark as a texture beside the drawing, never behind the words (brief section 4). */}
        <Box pos="relative" visibleFrom="sm" style={{ flexShrink: 0 }}>
          <TenantMark hostname={hostname} size={240} opacity={0.07} style={{ position: "absolute", left: "50%", top: "50%", transform: "translate(-50%, -50%)", pointerEvents: "none" }} />
          <Box pos="relative">
            <Home size="lg" />
          </Box>
        </Box>
      </Group>

      <Grid gap="xl">
        <Grid.Col span={{ base: 12, md: 8 }}>
          <Section title={t("home.modules")}>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
              {openers.map((opening) => (
                <Opener key={opening.module} opening={opening} drawIn={drawIn} />
              ))}
            </SimpleGrid>
          </Section>
        </Grid.Col>
        <Grid.Col span={{ base: 12, md: 4 }}>
          <Stack gap="xl">
            <QuickActionsSection />
            <RecentSection principal={me.id} />
          </Stack>
        </Grid.Col>
      </Grid>
    </Stack>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <Stack gap="sm" component="section" aria-labelledby={id}>
      <Title order={2} size="h4" id={id}>
        {title}
      </Title>
      {children}
    </Stack>
  );
}

/** One module in its accent: its mark, its title, one line, a spot drawing if it has one, and its first pages. */
function Opener({ opening, drawIn }: { opening: ModuleOpening; drawIn: boolean }) {
  const { t } = useTranslation();
  const id = useId();
  const accent = opening.opener?.accent ?? "primary";
  const Drawing = opening.opener?.drawing;
  return (
    <Paper withBorder p="lg" radius="md" component="article" aria-labelledby={id} data-opener={opening.module} style={{ borderTop: `3px solid var(--iron-accent-${accent})` }}>
      <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
        <Group align="flex-start" gap="md" wrap="nowrap">
          <ModuleMark name={opening.module} accent={accent} size={56} animate={drawIn} />
          <Stack gap={4}>
            <Title order={3} size="h4" id={id}>
              {t(opening.titleKey)}
            </Title>
            {opening.opener && (
              <Text size="sm" c="dimmed">
                {t(opening.opener.lineKey)}
              </Text>
            )}
          </Stack>
        </Group>
        {Drawing && <Drawing decorative size="sm" accent={`var(--iron-accent-${accent})`} />}
      </Group>
      <Stack gap={6} component="ul" mt="md" mb={0} pl={0} style={{ listStyle: "none" }}>
        {opening.entries.map((entry) => (
          <li key={entry.path}>
            <Anchor component={Link} to={entry.path} size="sm">
              <Group gap={6} wrap="nowrap" component="span">
                {entry.icon}
                {t(entry.labelKey)}
              </Group>
            </Anchor>
          </li>
        ))}
      </Stack>
    </Paper>
  );
}

/** The actions the principal may start from here; with none, the section is not there at all (ADR-0033). */
function QuickActionsSection() {
  const { t } = useTranslation();
  const { actions } = useQuickActions();
  if (actions.length === 0) {
    return null;
  }
  return (
    <Section title={t("home.quickActions")}>
      <Stack gap="xs" align="flex-start">
        {actions.map((action) => (
          <Button key={action.id} component={Link} to={action.path} variant="light" leftSection={action.icon}>
            {t(action.labelKey)}
          </Button>
        ))}
      </Stack>
    </Section>
  );
}

/** The last pages opened in this browser, those the principal may still open; or what will appear here. */
function RecentSection({ principal }: { principal: string }) {
  const { t } = useTranslation();
  const session = useSession();
  // Read once when the page opens: nothing on the home page changes while the person reads it.
  const [pages] = useState<RecentPage[]>(() => readRecent(principal));
  const visible = pages.filter((page) => {
    const route = routeAt(manifests, page.path);
    return route !== undefined && (route.permission === null || session.permissions.includes(route.permission ?? ""));
  });
  const moduleTitle = (module: string) => manifests.find((manifest) => manifest.name === module)?.titleKey ?? "";
  return (
    <Section title={t("home.recent.title")}>
      {visible.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("home.recent.empty")}
        </Text>
      ) : (
        <Stack gap={6} component="ul" m={0} pl={0} style={{ listStyle: "none" }}>
          {visible.map((page) => (
            <li key={page.path}>
              <Anchor component={Link} to={page.path} size="sm">
                {page.label ?? t(page.titleKey)}
              </Anchor>
              <Text span size="xs" c="dimmed">
                {" · "}
                {page.label !== undefined ? `${t(page.titleKey)}, ` : ""}
                {t(moduleTitle(page.module))}
              </Text>
            </li>
          ))}
        </Stack>
      )}
    </Section>
  );
}
