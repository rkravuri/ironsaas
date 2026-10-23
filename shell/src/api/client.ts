import { z } from "zod";

/** One field of a validation problem, as ProblemFactory writes it: the snake_case JSON path, a short code, the message. */
export const FieldProblemSchema = z.object({
  field: z.string(),
  code: z.string(),
  message: z.string(),
});
export type FieldProblem = z.infer<typeof FieldProblemSchema>;

/** RFC 9457 problem details as the API writes them (ADR-0009): error_code and correlation_id on every one, errors on validation. */
export const ProblemSchema = z.object({
  type: z.string().optional(),
  title: z.string().optional(),
  status: z.number().optional(),
  detail: z.string().optional(),
  instance: z.string().optional(),
  error_code: z.string().optional(),
  correlation_id: z.string().optional(),
  errors: z.array(FieldProblemSchema).optional(),
  /** The code of a recovery link, when a sign-in found the password past its maximum age (TF-012, ADR-0046). */
  recovery_token: z.string().optional(),
  /** How long to wait before a second factor is checked again, during a cooling period (TF-013 AC2, ADR-0047). */
  retry_after_seconds: z.number().optional(),
  /** The company login a person who signs in only through it is sent to (TF-014 AC2, ADR-0048). */
  provider_id: z.string().optional(),
  provider_name: z.string().optional(),
  /** Where a refused re-authentication through a company login returns the person, to do the action again (TF-248 AC6). */
  return_to: z.string().optional(),
  /** The pending change a change of sensitive configuration became, waiting for a second administrator (TF-068, ADR-0052). */
  change_id: z.string().optional(),
  expires_at: z.string().optional(),
});
export type Problem = z.infer<typeof ProblemSchema>;

/** The list envelope (TF-101) as Iron.Kernel.Api writes it (ListResponse.cs): data, paging meta and links. */
export const ListMetaSchema = z.object({
  page_number: z.number(),
  page_size: z.number(),
  total_count: z.number(),
  total_pages: z.number(),
  has_previous: z.boolean(),
  has_next: z.boolean(),
  page_size_capped: z.boolean(),
  max_page_size: z.number(),
});
export type ListMeta = z.infer<typeof ListMetaSchema>;

export const ListLinksSchema = z.object({
  self: z.string(),
  first: z.string(),
  previous: z.string().nullable(),
  next: z.string().nullable(),
  last: z.string(),
});

export const listSchema = <T extends z.ZodType>(item: T) =>
  z.object({
    data: z.array(item),
    meta: ListMetaSchema,
    links: ListLinksSchema,
  });

/** What a failed call throws: the problem's fields and the correlation id, which the error boundary shows. */
export class ApiError extends Error {
  readonly status: number;
  readonly errorCode: string | undefined;
  readonly correlationId: string | undefined;
  readonly detail: string | undefined;
  readonly errors: readonly FieldProblem[] | undefined;
  readonly recoveryToken: string | undefined;
  readonly retryAfterSeconds: number | undefined;
  /** The company login to use instead, when a password sign-in was refused with "Use your company login" (ADR-0048). */
  readonly provider: { id: string; name: string } | undefined;
  /** The page a refused re-authentication through a company login goes back to (TF-248 AC6, ADR-0048). */
  readonly returnTo: string | undefined;
  /** The pending change the request became, when the tenant asks a second administrator to approve it (ADR-0052). */
  readonly changeId: string | undefined;

  constructor(status: number, problem: Problem, correlationId: string | undefined) {
    super(problem.detail ?? problem.title ?? `The request failed with status ${status}.`);
    this.name = "ApiError";
    this.status = status;
    this.errorCode = problem.error_code;
    this.correlationId = problem.correlation_id ?? correlationId;
    this.detail = problem.detail;
    this.errors = problem.errors;
    this.recoveryToken = problem.recovery_token;
    this.retryAfterSeconds = problem.retry_after_seconds;
    this.provider =
      problem.provider_id !== undefined && problem.provider_name !== undefined
        ? { id: problem.provider_id, name: problem.provider_name }
        : undefined;
    this.returnTo = problem.return_to;
    this.changeId = problem.change_id;
  }

  /**
   * Not a failure: the change waits for a second administrator's approval (TF-068, ADR-0052). Nothing changed yet, so the
   * caller does not go on as if it had; the form says so in the information colour and the frame links to the change.
   */
  get isHeld(): boolean {
    return this.status === 202 && this.errorCode === changeHeld;
  }

  /** A high-risk action refused for an old sign-in (TF-248): the person confirms it is them, and it is sent again. */
  get needsReauthentication(): boolean {
    return this.status === 403 && this.errorCode === reauthenticationRequired;
  }

  get isUnauthenticated(): boolean {
    return this.status === 401;
  }

  get isForbidden(): boolean {
    return this.status === 403;
  }
}

export interface RequestOptions<T> {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  schema?: z.ZodType<T>;
  signal?: AbortSignal;
}

/** The code of a refusal for an old sign-in (TF-248, ADR-0047). */
export const reauthenticationRequired = "auth.reauthentication_required";

/** The code of a refusal while the tenant's policy requires a second factor the person has not got (ADR-0047). */
export const enrolmentRequired = "identity.mfa.enrolment_required";

type EnrolmentListener = () => void;
const enrolmentListeners = new Set<EnrolmentListener>();

/**
 * The shell's frame subscribes to learn that the tenant now requires a second factor of the person (ADR-0047): any call
 * answers 403 with the code, and the frame takes the person to enrolment. The session stays.
 */
export function onEnrolmentRequired(listener: EnrolmentListener): () => void {
  enrolmentListeners.add(listener);
  return () => enrolmentListeners.delete(listener);
}

/** Asks the person to confirm it is them; true when they did, false when they cancelled. */
export type Reauthenticate = () => Promise<boolean>;
let reauthenticator: Reauthenticate | undefined;

/**
 * The shell's one re-authentication dialog registers here (TF-248): a request refused for an old sign-in waits while the
 * person confirms it is them, and is sent again once, so the action goes on without the person repeating it. Without a
 * dialog, or when the person cancels, the refusal is thrown as any other.
 */
export function onReauthenticationRequired(handler: Reauthenticate): () => void {
  reauthenticator = handler;
  return () => {
    if (reauthenticator === handler) {
      reauthenticator = undefined;
    }
  };
}

type UnauthenticatedListener = (error: ApiError) => void;
const unauthenticatedListeners = new Set<UnauthenticatedListener>();

/** The session provider subscribes to learn that a session ended (401) from any call, and why. */
export function onUnauthenticated(listener: UnauthenticatedListener): () => void {
  unauthenticatedListeners.add(listener);
  return () => unauthenticatedListeners.delete(listener);
}

/** What one answer tells the session clock (ADR-0033): its status, and the server's clock from the `Date` header. */
export interface ResponseInfo {
  path: string;
  status: number;
  /** The server's time when it answered, in milliseconds; undefined when the answer had no `Date` header. */
  serverDate: number | undefined;
  /** This browser's time when the answer arrived. */
  receivedAt: number;
  /** The delegator whose delegation ended while this session acted for them, on the first answer after (ADR-0037). */
  actingEnded?: string;
  /** The first answer after an impersonation ended: the request ran as the impersonator again (TF-025 AC3). */
  impersonationEnded?: boolean;
}

/** The header the API sets on the first answer after an impersonation ended. */
export const impersonationEndedHeader = "Iron-Impersonation-Ended";

/** The code of a refusal while a document the person must accept has a new version (TF-028 AC1, ADR-0049). */
export const termsRequired = "identity.terms.acceptance_required";

type TermsListener = () => void;
const termsListeners = new Set<TermsListener>();

/** The shell's frame subscribes to learn that the person must accept a new version first: any call answers 403 with the code. */
export function onTermsRequired(listener: TermsListener): () => void {
  termsListeners.add(listener);
  return () => termsListeners.delete(listener);
}

/** The code of a change held for a second administrator's approval: the answer is 202 with a problem (ADR-0052). */
export const changeHeld = "changes.change.held";

type HeldListener = (error: ApiError) => void;
const heldListeners = new Set<HeldListener>();

/** The shell's frame subscribes to learn that a change the person made waits for approval, from any call (TF-068 AC1). */
export function onChangeHeld(listener: HeldListener): () => void {
  heldListeners.add(listener);
  return () => heldListeners.delete(listener);
}

/** The header the API sets on the first answer after a delegation the session acted with has ended. */
export const actingEndedHeader = "Iron-Acting-Ended";

type ResponseListener = (info: ResponseInfo) => void;
const responseListeners = new Set<ResponseListener>();

/** Every answer, before it is read: an answer other than 401 means the session was alive and the server counted it. */
export function onResponse(listener: ResponseListener): () => void {
  responseListeners.add(listener);
  return () => responseListeners.delete(listener);
}

/**
 * One way to call the API: same origin, the session cookie, JSON in and out, a problem turned into an ApiError.
 * Paths are absolute (`/api/v1/users`); the ingress routes them to the tenant's API (ADR-0020).
 */
export async function request<T = unknown>(path: string, options: RequestOptions<T> = {}): Promise<T> {
  try {
    return await send(path, options);
  } catch (error) {
    if (error instanceof ApiError && error.needsReauthentication && reauthenticator && (await reauthenticator())) {
      return send(path, options);
    }
    throw error;
  }
}

async function send<T>(path: string, options: RequestOptions<T>): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  const init: RequestInit = {
    method: options.method ?? "GET",
    credentials: "same-origin",
    headers,
  };
  if (options.signal) {
    init.signal = options.signal;
  }
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }

  const response = await fetch(path, init);
  const correlationId = response.headers.get("X-Correlation-Id") ?? undefined;
  const date = Date.parse(response.headers.get("Date") ?? "");
  const actingEnded = response.headers.get(actingEndedHeader) ?? undefined;
  const impersonationEnded = response.headers.get(impersonationEndedHeader) === "true";
  const info: ResponseInfo = {
    path,
    status: response.status,
    serverDate: Number.isNaN(date) ? undefined : date,
    receivedAt: Date.now(),
    ...(actingEnded ? { actingEnded } : {}),
    ...(impersonationEnded ? { impersonationEnded } : {}),
  };
  for (const listener of responseListeners) {
    listener(info);
  }

  if (!response.ok) {
    const problem = await readProblem(response);
    const error = new ApiError(response.status, problem, correlationId);
    if (error.isUnauthenticated) {
      for (const listener of unauthenticatedListeners) {
        listener(error);
      }
    }
    if (error.status === 403 && error.errorCode === enrolmentRequired) {
      for (const listener of enrolmentListeners) {
        listener();
      }
    }
    if (error.status === 403 && error.errorCode === termsRequired) {
      for (const listener of termsListeners) {
        listener();
      }
    }
    throw error;
  }

  if (response.status === 204) {
    return undefined as T;
  }

  // A held change answers 202 with a problem rather than the thing it would have made: thrown, so no caller reads it as
  // done. A 202 with its resource (an evidence pack accepted) is an ordinary answer.
  if (response.status === 202 && (response.headers.get("Content-Type") ?? "").includes("problem+json")) {
    const held = new ApiError(response.status, await readProblem(response), correlationId);
    if (held.isHeld) {
      for (const listener of heldListeners) {
        listener(held);
      }
    }
    throw held;
  }

  const json: unknown = await response.json();
  return options.schema ? options.schema.parse(json) : (json as T);
}

async function readProblem(response: Response): Promise<Problem> {
  try {
    const json: unknown = await response.json();
    const parsed = ProblemSchema.safeParse(json);
    return parsed.success ? parsed.data : {};
  } catch {
    return {};
  }
}
