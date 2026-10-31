import { z } from "zod";
import { listSchema, request } from "./client";

/**
 * Where an event stands with one subscriber (TF-111, ADR-0060): its attempts, the error of the last, its replays, and
 * since when it is held, while the unit no longer runs the subscriber's suite.
 */
export const EventDeliverySchema = z.object({
  subscriber: z.string(),
  status: z.enum(["pending", "handled", "dead", "held"]),
  attempts: z.number(),
  next_attempt_at: z.string(),
  last_error: z.string().nullable(),
  handled_at: z.string().nullable(),
  dead_at: z.string().nullable(),
  replays: z.number(),
  held_at: z.string().nullable(),
});
export type EventDelivery = z.infer<typeof EventDeliverySchema>;

/**
 * A domain event (TF-110, ADR-0060): what happened to which record, who acted, when, the request it came from, its data
 * (identifiers, codes, numbers and times, never a copy of the record), and one word for where it stands.
 */
export const DomainEventSchema = z.object({
  id: z.string(),
  position: z.number(),
  type: z.string(),
  version: z.number(),
  record_type: z.string(),
  record_id: z.string(),
  actor_id: z.string(),
  on_behalf_of_id: z.string().nullable(),
  occurred_at: z.string(),
  correlation_id: z.string().nullable(),
  data: z.record(z.string(), z.unknown()),
  status: z.enum(["pending", "handled", "dead", "held", "no_subscriber"]),
  deliveries: z.array(EventDeliverySchema),
});
export type DomainEvent = z.infer<typeof DomainEventSchema>;

export const DomainEventListSchema = listSchema(DomainEventSchema);

/** An event a subscriber parked after its last attempt, with the error. */
export const DeadLetterSchema = z.object({
  event_id: z.string(),
  position: z.number(),
  subscriber: z.string(),
  type: z.string(),
  version: z.number(),
  record_type: z.string(),
  record_id: z.string(),
  occurred_at: z.string(),
  attempts: z.number(),
  last_error: z.string().nullable(),
  dead_at: z.string().nullable(),
});
export type DeadLetter = z.infer<typeof DeadLetterSchema>;

export const DeadLetterListSchema = listSchema(DeadLetterSchema);

/** An event the unit's modules publish, as the catalogue lists it, with the subscriptions that read it. */
export const EventTypeSchema = z.object({
  name: z.string(),
  version: z.number(),
  module: z.string(),
  record_type: z.string(),
  description: z.string(),
  owner: z.string(),
  fields: z.array(z.object({ name: z.string(), type: z.string(), required: z.boolean(), personal: z.boolean() })),
  deprecated_on: z.string().nullable(),
  replaced_by: z.number().nullable(),
  subscribers: z.array(z.object({ name: z.string(), module: z.string(), description: z.string() })),
});
export type EventType = z.infer<typeof EventTypeSchema>;

export const EventRetentionSchema = z.object({
  days: z.number(),
  archived_through: z.string().nullable(),
  archives: z.number(),
});
export type EventRetention = z.infer<typeof EventRetentionSchema>;

export const ReplayResultSchema = z.object({
  subscriber: z.string(),
  events: z.number(),
  requeued: z.number(),
  already_handled: z.number(),
  added: z.number(),
});
export type ReplayResult = z.infer<typeof ReplayResultSchema>;

/** A replay to one subscriber, of named events or of a range of positions, with the reason the trail keeps. */
export interface ReplayRequest {
  subscriber: string;
  event_ids?: string[];
  from_position?: number;
  to_position?: number;
  reason: string;
}

export const fetchEventPage = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/events${query ? `?${query}` : ""}`, {
    schema: DomainEventListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchEvent = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/events/${encodeURIComponent(id)}`, { schema: DomainEventSchema, ...(signal ? { signal } : {}) });

export const fetchDeadLetterPage = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/events/dead-letters${query ? `?${query}` : ""}`, {
    schema: DeadLetterListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchEventTypes = (signal?: AbortSignal) =>
  request("/api/v1/events/types", { schema: z.array(EventTypeSchema), ...(signal ? { signal } : {}) });

export const fetchEventRetention = (signal?: AbortSignal) =>
  request("/api/v1/events/retention", { schema: EventRetentionSchema, ...(signal ? { signal } : {}) });

export const replayEvents = (replay: ReplayRequest) =>
  request("/api/v1/events/replay", { method: "POST", body: replay, schema: ReplayResultSchema });
