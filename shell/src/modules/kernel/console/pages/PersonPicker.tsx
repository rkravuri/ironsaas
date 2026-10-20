import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Select, type SelectProps } from "@mantine/core";
import { useDebouncedValue } from "@mantine/hooks";
import { searchDirectory, type DirectoryEntry } from "@/api/identity";

export interface PersonPickerProps extends Omit<SelectProps, "data" | "searchable" | "onSearchChange" | "searchValue" | "filter"> {
  /** People who cannot be chosen here, such as the person delegating. */
  exclude?: readonly string[];
}

/**
 * A person of this tenant, found by two letters or more of their name or login (the directory, ADR-0037): anyone may
 * delegate, including people who may not list users, so the answer carries a name and a home unit only.
 */
export function PersonPicker({ exclude = [], ...props }: PersonPickerProps) {
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
  const [chosen, setChosen] = useState<DirectoryEntry | undefined>();

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
        .map((entry) => ({ value: entry.id, label: entry.home_unit ? t("people.withUnit", { name: entry.display_name, unit: entry.home_unit }) : entry.display_name })),
    [entries, exclude, t],
  );

  return (
    <Select
      searchable
      clearable
      searchValue={search}
      onSearchChange={setSearch}
      filter={({ options }) => options}
      data={data}
      nothingFoundMessage={debounced.length < 2 ? t("people.typeMore") : found.isFetching ? t("people.searching") : t("people.nobody")}
      {...props}
      onChange={(value, option) => {
        setChosen(value ? entries.get(value) : undefined);
        props.onChange?.(value, option);
      }}
    />
  );
}
