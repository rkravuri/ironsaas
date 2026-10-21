import type { TFunction } from "i18next";
import { ApiError } from "@/api/client";
import { isPasskeyCancelled } from "./webauthn";

/** A refusal in words the person can act on, with the correlation id when the API gave one. */
export interface ShownProblem {
  message: string;
  correlationId: string | undefined;
}

/**
 * What went wrong with a second factor, in the catalogue's words (ADR-0047). The API's codes are mapped one by one; a
 * passkey the person cancelled is named as such; anything else is the unexpected error with its correlation id.
 */
export function mfaProblem(error: unknown, t: TFunction): ShownProblem {
  if (isPasskeyCancelled(error)) {
    return { message: t("mfa.errors.passkeyCancelled"), correlationId: undefined };
  }
  if (!(error instanceof ApiError)) {
    return { message: t("errors.unexpected"), correlationId: undefined };
  }
  const words: Record<string, string> = {
    "identity.second_factor.invalid": t("mfa.errors.invalid"),
    "identity.second_factor.cooling": t("mfa.errors.cooling", { count: error.retryAfterSeconds ?? 60 }),
    "identity.login.locked": t("login.locked"),
    "identity.sign_in.expired": t("mfa.errors.expired"),
    "identity.factor.invalid": t("mfa.errors.factorInvalid"),
    "identity.factor.not_allowed": t("mfa.errors.notAllowed"),
    "identity.factor.app_exists": t("mfa.errors.appExists"),
    "identity.factor.last": t("mfa.errors.last"),
    "identity.factor.not_found": t("mfa.errors.noPasskey"),
    "identity.reauthentication.invalid": t("mfa.reauth.invalid"),
    "identity.passkey_sign_in.off": t("mfa.errors.passkeyOff"),
  };
  return {
    message: (error.errorCode && words[error.errorCode]) ?? error.message ?? t("errors.unexpected"),
    correlationId: error.correlationId,
  };
}

/** The sign-in under way has ended: the person starts again from the password. */
export function signInEnded(error: unknown): boolean {
  return (
    error instanceof ApiError &&
    (error.errorCode === "identity.sign_in.expired" || error.errorCode === "identity.login.locked")
  );
}

/** Where My profile's Security tab opens with "Add a second factor", after a sign-in by recovery code (TF-013 AC3). */
export const enrolNewFactorPath = "/me?tab=security&add=1";
