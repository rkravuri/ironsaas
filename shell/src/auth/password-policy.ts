import { useQuery } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { fetchPasswordPolicy, type PasswordPolicy } from "@/api/identity";

/** The rules the API names in a refusal, as the field problem's code on `password` (TF-012, ADR-0046). */
export const passwordRules = ["too_short", "too_long", "is_login_id", "common", "reused"] as const;
export type PasswordRule = (typeof passwordRules)[number];

/** The policy until the API answers: the defaults, so the words are right for most tenants from the first paint. */
export const defaultPolicy: PasswordPolicy = {
  minimum_length: 12,
  maximum_length: 1024,
  common_check: true,
  history_depth: 10,
  maximum_age_days: 0,
  warning_days: 14,
};

/**
 * The tenant's password policy, from the anonymous endpoint, so the forms of people without a session have it too.
 * `enabled: false` keeps to the defaults without asking, for a part of the page that may not need it.
 */
export function usePasswordPolicy({ enabled = true }: { enabled?: boolean } = {}): PasswordPolicy {
  const policy = useQuery({
    queryKey: ["auth", "password-policy"],
    queryFn: ({ signal }) => fetchPasswordPolicy(signal),
    staleTime: 300_000,
    enabled,
  });
  return policy.data ?? defaultPolicy;
}

/** The rule a refusal names, when the API refused the password itself. */
export function failedRule(error: unknown): PasswordRule | undefined {
  if (!(error instanceof ApiError)) {
    return undefined;
  }
  const code = error.errors?.find((problem) => problem.field === "password")?.code;
  return passwordRules.find((rule) => rule === code);
}
