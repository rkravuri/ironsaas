import { useId, useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { Button, Kbd, Text, VisuallyHidden } from "@mantine/core";
import { Spotlight, createSpotlight, useSpotlight } from "@mantine/spotlight";
import { IconHome, IconSearch } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { onMac } from "@/components/shortcuts";
import { manifests } from "@/modules";
import { navigationFor } from "@/modules/registry";
import { useQuickActions } from "./quick-actions";

/** The search's own store: the action it has chosen is the one its input points a screen reader at. */
const [searchStore, searchActions] = createSpotlight();

interface SearchOption {
  id: string;
  label: string;
  description: string;
  icon: ReactNode;
  open: () => void;
}

interface SearchGroup {
  label: string;
  options: SearchOption[];
}

/** The options a query matches, as Mantine's own filter orders them: by the label first, then by the module's name. */
function matching(groups: readonly SearchGroup[], query: string): SearchGroup[] {
  const wanted = query.trim().toLowerCase();
  if (wanted === "") {
    return [...groups];
  }
  return groups
    .map((group) => {
      const byLabel = group.options.filter((option) => option.label.toLowerCase().includes(wanted));
      const byModule = group.options.filter((option) => !byLabel.includes(option) && option.description.toLowerCase().includes(wanted));
      return { ...group, options: [...byLabel, ...byModule] };
    })
    .filter((group) => group.options.length > 0);
}

/**
 * The search (TF-170, ADR-0033): Ctrl+K or Command+K, or the button, opens a palette of every page the principal may
 * open and the quick actions it may perform, found by name or by module. Enter opens, Escape closes, and focus goes
 * back where it was (the palette is a Mantine modal). It reaches only what the navigation and /me/access allow.
 *
 * It is a combobox, so a screen reader hears which action Enter will open: the input points at the chosen option
 * (aria-activedescendant) in a listbox of options grouped by kind. Spotlight's own palette gives its actions as buttons
 * the input does not point at, so the palette is built from Spotlight's parts with those roles.
 */
export function Search() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const quick = useQuickActions();
  const { selected, query } = useSpotlight(searchStore);
  // Spotlight moves its choice with the arrows without telling the store's subscribers (Mantine's store updates it
  // quietly), so after each key, once Spotlight's own handler has run, the search draws again and reads the choice.
  const [, redraw] = useState(0);
  const followKeys = () => void Promise.resolve().then(() => redraw((count) => count + 1));
  const listId = `search-${useId().replace(/[^a-zA-Z0-9_-]/g, "")}`;
  const navigation = useMemo(() => navigationFor(manifests, session.permissions, { strict: import.meta.env.DEV }, session.entitlements.entitled), [session.permissions, session.entitlements]);

  const groups = useMemo(() => {
    const all: SearchGroup[] = [
      {
        label: t("search.pages"),
        options: [
          { id: "page:/", label: t("home.title"), description: t("search.shell"), icon: <IconHome size={18} aria-hidden="true" />, open: () => void navigate("/") },
          ...navigation.flatMap((group) =>
            group.entries.map((entry) => ({ id: `page:${entry.path}`, label: t(entry.labelKey), description: t(group.titleKey), icon: entry.icon, open: () => void navigate(entry.path) })),
          ),
        ],
      },
    ];
    if (quick.actions.length > 0) {
      const titles = new Map(manifests.map((manifest) => [manifest.name, manifest.titleKey]));
      all.push({
        label: t("search.actions"),
        options: quick.actions.map((action) => ({
          id: `action:${action.id}`,
          label: t(action.labelKey),
          description: t(titles.get(action.module) ?? ""),
          icon: action.icon,
          open: () => void navigate(action.path),
        })),
      });
    }
    return all;
  }, [t, navigate, navigation, quick.actions]);

  const shown = matching(groups, query);
  // Spotlight counts the options in the order they are drawn; the same count names each one here.
  const optionId = (index: number) => `${listId}-option-${index}`;
  const count = shown.reduce((sum, group) => sum + group.options.length, 0);
  const starts = shown.map((_, position) => shown.slice(0, position).reduce((sum, group) => sum + group.options.length, 0));
  const active = selected >= 0 && selected < count ? optionId(selected) : undefined;

  return (
    <Spotlight.Root
      store={searchStore}
      // Spotlight dims the chosen action's module to 70 percent, under 4.5:1 on the primary fill; the white in full keeps
      // the pair the contrast test holds (ADR-0025).
      styles={{ action: { "--action-description-opacity": "1" } }}
      shortcut={["mod + K"]}
      scrollable
      maxHeight={420}
    >
      <Spotlight.Search
        leftSection={<IconSearch size={20} aria-hidden="true" />}
        placeholder={t("search.placeholder")}
        aria-label={t("search.label")}
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={count > 0}
        aria-controls={count > 0 ? listId : undefined}
        aria-activedescendant={active}
        onKeyDown={followKeys}
      />
      {count > 0 ? (
        <Spotlight.ActionsList id={listId} role="listbox" aria-label={t("search.results")}>
          {shown.map((group, position) => (
            <Spotlight.ActionsGroup key={group.label} label={group.label} role="group" aria-label={group.label}>
              {group.options.map((option, offset) => {
                const index = (starts[position] ?? 0) + offset;
                return (
                  <Spotlight.Action
                    key={option.id}
                    // Spotlight's action keeps an id prop for itself and never puts it on its element, so the option's
                    // own id, which the input points at, is set on the element.
                    ref={(element: HTMLButtonElement | null) => {
                      if (element) {
                        element.id = optionId(index);
                      }
                    }}
                    role="option"
                    aria-selected={index === selected}
                    label={option.label}
                    description={option.description}
                    leftSection={option.icon}
                    highlightQuery
                    onClick={option.open}
                  />
                );
              })}
            </Spotlight.ActionsGroup>
          ))}
        </Spotlight.ActionsList>
      ) : (
        <Spotlight.Empty role="status">{t("search.nothingFound")}</Spotlight.Empty>
      )}
    </Spotlight.Root>
  );
}

/** The visible way into the search, with its keys written beside it. */
export function SearchButton() {
  const { t } = useTranslation();
  const keys = `${onMac() ? "⌘" : "Ctrl"} K`;
  return (
    <Button
      variant="default"
      size="sm"
      onClick={() => searchActions.open()}
      leftSection={<IconSearch size={16} aria-hidden="true" />}
      rightSection={
        <Kbd size="xs" visibleFrom="sm">
          {keys}
        </Kbd>
      }
      aria-keyshortcuts="Control+K Meta+K"
    >
      <Text span size="sm" visibleFrom="sm">
        {t("search.open")}
      </Text>
      <VisuallyHidden hiddenFrom="sm">{t("search.open")}</VisuallyHidden>
    </Button>
  );
}
