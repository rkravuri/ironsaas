import type { TFunction } from "i18next";
import type { ConflictGrant, ConflictRule, GrantConflict } from "@/api/authorization";

/** One grant of a conflict in words: the role and where it applies, and "by delegation" for one held through a delegation. */
export function conflictGrantWords(t: TFunction, grant: ConflictGrant): string {
  const unit = grant.unit_name ?? "";
  const words =
    grant.scope_kind === "unit"
      ? t("conflicts.grant.unit", { role: grant.role, unit })
      : grant.scope_kind === "unit_with_children"
        ? t("conflicts.grant.unitWithChildren", { role: grant.role, unit })
        : grant.scope_kind === "record"
          ? t("conflicts.grant.record", { role: grant.role })
          : t("conflicts.grant.tenant", { role: grant.role });
  return grant.source === "delegation" ? t("conflicts.grant.delegated", { grant: words }) : words;
}

/** What a rule keeps apart, in words: its two sides by name. */
export function ruleSidesWords(t: TFunction, rule: ConflictRule): string {
  return t("conflicts.rules.sides", {
    first: rule.first.name,
    second: rule.second.name,
  });
}

/** A conflict a grant would create, in words: the rule, the grant it meets, and where the two would overlap. */
export function grantConflictWords(t: TFunction, name: string, conflict: GrantConflict): string {
  const where = conflict.unit_name !== null ? t("conflicts.preview.atUnit", { unit: conflict.unit_name }) : t("conflicts.preview.wholeTenant");
  return t("conflicts.preview.line", {
    rule: conflict.rule_name,
    name,
    grant: conflictGrantWords(t, conflict.with),
    where,
  });
}
