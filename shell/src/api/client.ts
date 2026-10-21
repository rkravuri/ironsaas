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

  constructor(status: number, problem: Problem, correlationId: string | undefined) {
    super(problem.detail ?? problem.title ?? `The request failed with status ${status}.`);
    this.name = "ApiError";
    this.status = status;
    this.errorCode = problem.error_code;
    this.correlationId = problem.correlation_id ?? correlationId;
    this.detail = problem.detail;
    this.errors = problem.errors;
    this.recoveryToken = problem.recovery_token;
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
  const info: ResponseInfo = {
    path,
    status: response.status,
    serverDate: Number.isNaN(date) ? undefined : date,
    receivedAt: Date.now(),
    ...(actingEnded ? { actingEnded } : {}),
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
    throw error;
  }

  if (response.status === 204) {
    return undefined as T;
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
