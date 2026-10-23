import type { TFunction } from "i18next";
import { ApiError } from "@/api/client";
import { mfaProblem, type ShownProblem } from "./mfa-problems";

/** A refusal of a signature in words the person can act on (ADR-0051), with the correlation id; a factor's as at sign-in. */
export function signingProblem(error: unknown, t: TFunction): ShownProblem {
  if (error instanceof ApiError && error.errorCode?.startsWith("compliance.")) {
    const code = error.errorCode.slice(error.errorCode.lastIndexOf(".") + 1);
    return {
      message: t(`signing.errors.${code}`, { defaultValue: error.message }),
      correlationId: error.correlationId,
    };
  }
  return mfaProblem(error, t);
}
