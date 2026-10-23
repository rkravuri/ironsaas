import type { TFunction } from "i18next";
import type { AuditEvent } from "@/api/audit";

/** The system principal's id (Iron.Kernel SystemPrincipal): jobs act as it. */
export const systemPrincipalId = "00000000-0000-0000-0000-000000000001";

/** Who acted, in words: the person's name, the system for a job, an operator signed in as them, or a delegate for someone. */
export function actorWords(event: AuditEvent, t: TFunction): string {
  const actor = event.actor_id === systemPrincipalId ? t("audit.actor.system") : (event.actor_name ?? event.actor_id);
  if (event.impersonator_id) {
    return t("history.impersonated", {
      actor,
      impersonator: event.impersonator_name ?? event.impersonator_id,
    });
  }

  if (event.on_behalf_of_id) {
    return t("audit.actor.onBehalfOf", { actor, onBehalfOf: event.on_behalf_of_name ?? event.on_behalf_of_id });
  }

  return actor;
}

export const outcomeWords = (outcome: string, t: TFunction): string =>
  t(`audit.outcomes.${outcome}`, { defaultValue: outcome });

export const severityWords = (severity: string, t: TFunction): string =>
  t(`audit.severities.${severity}`, { defaultValue: severity });

/** A retention class's name and what it covers, from the catalogue; the server's English text when it has none. */
export const retentionClassName = (code: string, t: TFunction): string =>
  t(`retention.classes.${code}.name`, { defaultValue: code });

/** Days as a person reads them: whole years when they are, otherwise days. */
export function periodWords(days: number, t: TFunction): string {
  return days % 365 === 0 ? t("retention.years", { count: days / 365 }) : t("retention.days", { count: days });
}
