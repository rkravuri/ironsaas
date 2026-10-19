import { useMemo, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { Button, Kbd, Text, VisuallyHidden } from "@mantine/core";
import { Spotlight, spotlight, type SpotlightActionGroupData } from "@mantine/spotlight";
import { IconHome, IconSearch } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { onMac } from "@/components/shortcuts";
import { manifests } from "@/modules";
import { navigationFor } from "@/modules/registry";
import { useQuickActions } from "./quick-actions";

/**
 * The search (TF-170, ADR-0033): Ctrl+K or Command+K, or the button, opens a palette of every page the principal may
 * open and the quick actions it may perform, found by name or by module. Enter opens, Escape closes, and focus goes
 * back where it was (the palette is a Mantine modal). It reaches only what the navigation and /me/access allow.
 */
export function Search() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const quick = useQuickActions();
  const navigation = useMemo(() => navigationFor(manifests, session.permissions, { strict: import.meta.env.DEV }), [session.permissions]);

  const groups: SpotlightActionGroupData[] = [
    {
      group: t("search.pages"),
      actions: [
        { id: "page:/", label: t("home.title"), description: t("search.shell"), leftSection: <IconHome size={18} aria-hidden="true" />, onClick: () => void navigate("/") },
        ...navigation.flatMap((group) =>
          group.entries.map((entry) => ({ id: `page:${entry.path}`, label: t(entry.labelKey), description: t(group.titleKey), leftSection: entry.icon, onClick: () => void navigate(entry.path) })),
        ),
      ],
    },
  ];
  if (quick.actions.length > 0) {
    const titles = new Map(manifests.map((manifest) => [manifest.name, manifest.titleKey]));
    groups.push({
      group: t("search.actions"),
      actions: quick.actions.map((action) => ({ id: `action:${action.id}`, label: t(action.labelKey), description: t(titles.get(action.module) ?? ""), leftSection: action.icon, onClick: () => void navigate(action.path) })),
    });
  }

  return (
    <Spotlight
      actions={groups}
      // Spotlight dims the chosen action's module to 70 percent, under 4.5:1 on the primary fill; the white in full keeps
      // the pair the contrast test holds (ADR-0025).
      styles={{ action: { "--action-description-opacity": "1" } as CSSProperties }}
      shortcut={["mod + K"]}
      highlightQuery
      nothingFound={t("search.nothingFound")}
      scrollable
      maxHeight={420}
      searchProps={{ leftSection: <IconSearch size={20} aria-hidden="true" />, placeholder: t("search.placeholder"), "aria-label": t("search.label") }}
    />
  );
}

/** The visible way into the search, with its keys written beside it. */
export function SearchButton() {
  const { t } = useTranslation();
  const keys = `${onMac() ? "⌘" : "Ctrl"} K`;
  return (
    <Button variant="default" size="sm" onClick={() => spotlight.open()} leftSection={<IconSearch size={16} aria-hidden="true" />} rightSection={<Kbd size="xs" visibleFrom="sm">{keys}</Kbd>} aria-keyshortcuts="Control+K Meta+K">
      <Text span size="sm" visibleFrom="sm">
        {t("search.open")}
      </Text>
      <VisuallyHidden hiddenFrom="sm">{t("search.open")}</VisuallyHidden>
    </Button>
  );
}
