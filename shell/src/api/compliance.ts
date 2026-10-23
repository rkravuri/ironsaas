import { z } from "zod";
import { listSchema, request } from "./client";
import type { SecondFactorProof } from "./identity";

/** What a signature means (TF-063, ADR-0051). */
export const meanings = ["approved", "rejected", "acknowledged", "signed", "reviewed"] as const;
export type Meaning = (typeof meanings)[number];

export const SigningPolicySchema = z.object({
  required: z.boolean(),
  meanings: z.array(z.string()),
  reauthenticate: z.boolean(),
});
export type SigningPolicy = z.infer<typeof SigningPolicySchema>;

/** A document type that can be signed, with the tenant's policy and its module's default. */
export const SignableTypeSchema = z.object({
  record_type: z.string(),
  name: z.string(),
  meanings: z.array(z.string()),
  always_signed: z.boolean(),
  policy: SigningPolicySchema,
  default: SigningPolicySchema,
});
export type SignableType = z.infer<typeof SignableTypeSchema>;

export const complianceQueryKey = ["compliance"] as const;

export const fetchSignableTypes = (signal?: AbortSignal) =>
  request("/api/v1/signatures/types", {
    schema: z.object({ data: z.array(SignableTypeSchema) }),
    ...(signal ? { signal } : {}),
  });

/**
 * What the next signature asks for (ADR-0051, Part 11 section 11.200): the login identifier at the first of a session,
 * the password, the second factor, or a fresh sign-in at the person's company login.
 */
export const SigningChallengeSchema = z.object({
  first_in_session: z.boolean(),
  login_id: z.boolean(),
  password: z.boolean(),
  second_factor: z.boolean(),
  methods: z.array(z.string()),
  passkey_options: z.record(z.string(), z.unknown()).nullable(),
  provider_id: z.string().nullable(),
  provider_name: z.string().nullable(),
  provider_confirmed: z.boolean(),
  reauthenticate: z.boolean(),
  policy: SigningPolicySchema,
});
export type SigningChallenge = z.infer<typeof SigningChallengeSchema>;

export const fetchSigningChallenge = (recordType: string, signal?: AbortSignal) =>
  request(`/api/v1/signatures/challenge?record_type=${encodeURIComponent(recordType)}`, {
    schema: SigningChallengeSchema,
    ...(signal ? { signal } : {}),
  });

/** The credentials a signature carries, as the dialog collected them. */
export type SigningCredentials = { login_id?: string; password?: string } & Partial<{
  code: string;
  recovery_code: string;
  passkey: unknown;
}>;

export const credentialsWith = (
  base: { login_id?: string; password?: string },
  proof: SecondFactorProof | undefined,
): SigningCredentials => ({ ...base, ...(proof ?? {}) });

/** A signature (TF-063 AC2, AC3): who, how, when, what it means, its version, its hash checked, and the record since. */
export const SignatureSchema = z.object({
  id: z.string(),
  record_type: z.string(),
  record_id: z.string(),
  record_version: z.number(),
  signer_id: z.string(),
  signer_name: z.string(),
  signer_login_id: z.string(),
  method: z.string(),
  meaning: z.string(),
  comment: z.string().nullable(),
  signed_at: z.string(),
  snapshot_hash: z.string(),
  hash_matches: z.boolean(),
  current_version: z.number().nullable(),
  record_changed: z.boolean(),
});
export type Signature = z.infer<typeof SignatureSchema>;

export const fetchSignatures = (recordType: string, recordId: string, signal?: AbortSignal) =>
  request(
    `/api/v1/signatures?record_type=${encodeURIComponent(recordType)}&record_id=${encodeURIComponent(recordId)}`,
    { schema: z.object({ data: z.array(SignatureSchema) }), ...(signal ? { signal } : {}) },
  );

/** The snapshot a signature signed: the canonical text as kept and its hash recomputed now. */
export const SignedSnapshotSchema = z.object({
  signature: SignatureSchema,
  type_name: z.string(),
  canonical: z.string(),
  form: z.number(),
  recomputed_hash: z.string(),
  content: z.record(z.string(), z.unknown()),
});
export type SignedSnapshot = z.infer<typeof SignedSnapshotSchema>;

export const fetchSignedSnapshot = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/signatures/${encodeURIComponent(id)}/snapshot`, {
    schema: SignedSnapshotSchema,
    ...(signal ? { signal } : {}),
  });

export const ReviewSearchSchema = z.object({ label: z.string(), link: z.string() });
export type ReviewSearch = z.infer<typeof ReviewSearchSchema>;

export const auditReviewOutcomes = ["no_findings", "findings"] as const;
export type AuditReviewOutcome = (typeof auditReviewOutcomes)[number];

/** A review of the audit trail as the list shows it (TF-219 AC3). */
export const AuditReviewRowSchema = z.object({
  id: z.string(),
  period_from: z.string(),
  period_to: z.string(),
  outcome: z.string(),
  reviewer_id: z.string(),
  reviewer_name: z.string(),
  recorded_at: z.string(),
});
export type AuditReviewRow = z.infer<typeof AuditReviewRowSchema>;

export const AuditReviewSchema = AuditReviewRowSchema.extend({
  unit_ids: z.array(z.string()),
  event_types: z.array(z.string()),
  notes: z.string().nullable(),
  searches: z.array(ReviewSearchSchema),
  signature: SignatureSchema.nullable(),
});
export type AuditReview = z.infer<typeof AuditReviewSchema>;

export const fetchAuditReviews = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/audit-reviews${query ? `?${query}` : ""}`, {
    schema: listSchema(AuditReviewRowSchema),
    ...(signal ? { signal } : {}),
  });

export const fetchAuditReview = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/audit-reviews/${encodeURIComponent(id)}`, {
    schema: AuditReviewSchema,
    ...(signal ? { signal } : {}),
  });

export const ReviewMonthSchema = z.object({
  from: z.string(),
  to: z.string(),
  reviews: z.array(z.string()),
  outcomes: z.array(z.string()),
});
export type ReviewMonth = z.infer<typeof ReviewMonthSchema>;

/** The last months, newest first, each with the reviews that cover it, so a month nobody reviewed shows. */
export const fetchReviewCoverage = (months: number, signal?: AbortSignal) =>
  request(`/api/v1/audit-reviews/coverage?months=${months}`, {
    schema: z.object({ months: z.array(ReviewMonthSchema) }),
    ...(signal ? { signal } : {}),
  });

export interface AuditReviewInput {
  period_from: string;
  period_to: string;
  unit_ids: string[];
  event_types: string[];
  outcome: AuditReviewOutcome;
  notes: string | null;
  searches: ReviewSearch[];
}

/** Records a review and signs it "reviewed" with the signer's credentials (TF-219 AC3, ADR-0051). */
export const recordAuditReview = (input: AuditReviewInput, signature: SigningCredentials) =>
  request("/api/v1/audit-reviews", { method: "POST", body: { ...input, signature }, schema: AuditReviewSchema });
