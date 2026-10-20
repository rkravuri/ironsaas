import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Select, type SelectProps } from "@mantine/core";
import { useDebouncedValue } from "@mantine/hooks";
import { searchDirectory, type DirectoryEntry } from "@/api/identity";

export interface PersonPickerProps extends Omit<
  SelectProps,
  "data" | "searchable" | "onSearchChange" | "searchValue" | "filter"
> {
  /** People who cannot be chosen here, such as the person delegating. */
  exclude?: readonly string[];
  /** A person chosen already, such as the one whose record the page was opened from: shown by name before any search. */
  initial?: DirectoryEntry | undefined;
}

/**
 * A person of this tenant, found by two letters or more of their name or login (the directory, ADR-0037): anyone may
 * delegate, including people who may not list users, so the answer carries a name and a home unit only.
 */
export function PersonPicker({ exclude = [], initial, ...props }: PersonPickerProps) {
  const { t } = useTranslation();
  const [search, setSearch] = useState("");
  const [debounced] = useDebouncedValue(search.trim(), 250);
  const found = useQuery({
    queryKey: ["identity", "directory", debounced],
    queryFn: ({ signal }) => searchDirectory(debounced, signal),
    enabled: debounced.length >= 2,
    staleTime: 30_000,
  });
  // The chosen person stays in the list, so the field keeps showing their name after a new search.
  const [chosen, setChosen] = useState<DirectoryEntry | undefined>(initial);

  const entries = useMemo(() => {
    const shown = new Map<string, DirectoryEntry>();
    for (const entry of found.data?.data ?? []) {
      shown.set(entry.id, entry);
    }
    if (chosen && chosen.id === props.value) {
      shown.set(chosen.id, chosen);
    }
    return shown;
  }, [found.data, chosen, props.value]);

  const data = useMemo(
    () =>
      [...entries.values()]
        .filter((entry) => !exclude.includes(entry.id))
        .map((entry) => ({
          value: entry.id,
          label: entry.home_unit
            ? t("people.withUnit", { name: entry.display_name, unit: entry.home_unit })
            : entry.display_name,
        })),
    [entries, exclude, t],
  );

  // The list opens only when it has people: a list without options is not a list (WCAG, aria-required-children). How
  // the search stands is said under the field instead.
  const more = (found.data?.meta.total_count ?? 0) > (found.data?.data.length ?? 0);
  const state =
    debounced.length < 2
      ? undefined
      : found.isFetching
        ? t("people.searching")
        : data.length === 0
          ? t("people.nobody")
          : more
            ? t("people.more")
            : undefined;
  const { description, ...rest } = props;
  return (
    <Select
      searchable
      clearable
      searchValue={search}
      onSearchChange={setSearch}
      filter={({ options }) => options}
      data={data}
      {...rest}
      description={
        state === undefined ? (
          description
        ) : (
          <>
            {description} {state}
          </>
        )
      }
      onChange={(value, option) => {
        setChosen(value ? entries.get(value) : undefined);
        props.onChange?.(value, option);
      }}
    />
  );
}
