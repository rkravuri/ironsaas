import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Select } from "@mantine/core";
import { auditQueryKey, fetchImpersonators, type SignedInAs } from "@/api/audit";

const kinds: readonly SignedInAs["kind"][] = ["operator", "break_glass", "person"];

/**
 * The audit search's "signed in as them by" filter (TF-025, ADR-0049, ADR-0050): everyone the trail names as having
 * signed in as someone, and every break-glass account, by name and grouped: the platform's operators first, then the
 * break-glass accounts, then the tenant's people. The value is "field:id", the list field that finds their events.
 */
export function SignedInByPicker({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const { t } = useTranslation();
  const found = useQuery({
    queryKey: [...auditQueryKey, "impersonators"],
    queryFn: ({ signal }) => fetchImpersonators(signal),
    staleTime: 60_000,
  });
  const entries = found.data?.data ?? [];
  const data = kinds
    .map((kind) => ({
      group: t(`audit.filters.signedInGroups.${kind}`),
      items: entries
        .filter((entry) => entry.kind === kind)
        .map((entry) => ({ value: `${entry.field}:${entry.id}`, label: entry.name })),
    }))
    .filter((group) => group.items.length > 0);

  // A list without options is not a list (WCAG, aria-required-children): with nobody to offer, the field says so.
  return (
    <Select
      label={t("audit.filters.impersonator")}
      placeholder={
        entries.length === 0 ? t("audit.filters.impersonatorNone") : t("audit.filters.impersonatorPlaceholder")
      }
      data={data}
      value={value === "" ? null : value}
      onChange={(next) => onChange(next ?? "")}
      searchable
      clearable
      disabled={entries.length === 0}
      nothingFoundMessage={t("audit.filters.impersonatorNothing")}
      w={260}
    />
  );
}
