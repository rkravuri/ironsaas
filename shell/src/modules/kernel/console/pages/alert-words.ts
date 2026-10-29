import type { TFunction } from "i18next";
import type { AlertRule, SecurityAlert } from "@/api/monitoring";

export const manageAlerts = "monitoring.alerts.manage";
export const manageRules = "monitoring.rules.manage";

/** Whom or what an alert is about, in words: the person by name where the unit knows them (ADR-0056). */
export function groupWords(alert: SecurityAlert, t: TFunction): string {
  if (alert.kind === "failing") {
    return t("securityAlerts.group.rule");
  }
  const value = alert.group_name ?? alert.group_value;
  return t(`securityAlerts.group.${alert.group_kind}`, { value, defaultValue: value });
}

/** What an alert counted: how many, against which threshold, within which window. */
export function countWords(alert: SecurityAlert, t: TFunction): string {
  return alert.kind === "failing"
    ? t("securityAlerts.failingCount")
    : t("securityAlerts.counted", { count: alert.count, threshold: alert.threshold, minutes: alert.window_minutes });
}

/** A rule's thresholds in words: each event at once, a count within a window, or a limit of records. */
export function thresholdWords(rule: AlertRule, values: NonNullable<AlertRule["effective"]>, t: TFunction): string {
  if (values.above != null) {
    return t("alertRules.above", { count: values.above });
  }
  if (rule.at_once) {
    return t("alertRules.atOnce");
  }
  return t("alertRules.within", { count: values.threshold, minutes: values.window_minutes });
}
