import { useSession } from "./session-context";

/** Reading the audit trail, which a record's History tab does (TF-064 AC1). */
export const readAuditTrail = "audit.events.read";

/** Whether this person may read the audit trail, and so a record's History (TF-064 AC1, TF-062). */
export function useReadsAuditTrail(): boolean {
  return useSession().permissions.includes(readAuditTrail);
}

/**
 * The record as the audit trail names it, for the record template's History tab, when this person may read the trail;
 * undefined otherwise, and for a record not saved yet, so the tab is not there (ADR-0050).
 */
export function useHistoryOf(
  recordType: string,
  recordId: string | undefined,
): { recordType: string; recordId: string } | undefined {
  const session = useSession();
  return recordId !== undefined && session.permissions.includes(readAuditTrail) ? { recordType, recordId } : undefined;
}
