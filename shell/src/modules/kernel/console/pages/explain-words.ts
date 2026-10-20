import type { TFunction } from "i18next";
import type { AccessExplanation, ExplainedCheck, ExplainedGrant } from "@/api/authorization";

/** Where a permission was checked, in words: the whole organisation, a unit, a record, or some unit for a list. */
export function whereWords(t: TFunction, facts: Record<string, string>): string {
  switch (facts.scope) {
    case "unit":
      return t("explain.where.unit", { unit: facts.unit ?? "" });
    case "record":
      return t("explain.where.record", { unit: facts.unit ?? "" });
    case "anywhere":
      return t("explain.where.anywhere");
    default:
      return t("explain.where.tenant");
  }
}

/** Where a granting assignment applies, in words. */
export function grantWords(t: TFunction, grant: ExplainedGrant): string {
  const unit = grant.unit?.name ?? "";
  switch (grant.scope_kind) {
    case "unit":
      return t("explain.grant.unit", { role: grant.role_name, unit });
    case "unit_with_children":
      return t("explain.grant.unitWithChildren", { role: grant.role_name, unit });
    case "record":
      return t("explain.grant.record", { role: grant.role_name });
    default:
      return t("explain.grant.tenant", { role: grant.role_name });
  }
}

/**
 * One check as a sentence, from the catalogue by check and reason with the check's facts; the API's own words when the
 * catalogue has none (a module's reason the shell does not know yet).
 */
export function checkWords(t: TFunction, check: ExplainedCheck, explanation: AccessExplanation): string {
  if (check.result === "not_reached") {
    return t("explain.reasons.notReached");
  }
  const reason =
    check.check === "entitlement" && check.result === "failed" && check.error_code
      ? (check.error_code.split(".").pop() ?? check.reason)
      : check.reason;
  const facts = check.facts;
  return t(`explain.reasons.${check.check}.${reason}`, {
    defaultValue: check.message,
    name: explanation.principal.name,
    where: whereWords(t, facts),
    permission: facts.permission_name ?? facts.permission ?? "",
    subject: facts.subject ?? "",
    flag: facts.flag ?? "",
    operatorReason: facts.reason ? t("explain.operatorReason", { reason: facts.reason }) : "",
    action: facts.action ?? "",
    delegator: explanation.acting_for?.name ?? "",
  });
}
