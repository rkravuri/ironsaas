import { z } from "zod";
import { listSchema, request } from "./client";

/** A visibility tag as the console lists it (TF-241, ADR-0057): its code for good, its name, and how many hold it. */
export const VisibilityTagSchema = z.object({
  id: z.string(),
  code: z.string(),
  name: z.string(),
  description: z.string().nullable(),
  status: z.enum(["active", "retired"]),
  holders: z.number(),
  updated_at: z.string(),
  version: z.number(),
});
export type VisibilityTag = z.infer<typeof VisibilityTagSchema>;
export const VisibilityTagListSchema = listSchema(VisibilityTagSchema);

/** A tag as a person's or a record's tags name it. */
export const TagSummarySchema = z.object({ code: z.string(), name: z.string(), active: z.boolean() });
export type TagSummary = z.infer<typeof TagSummarySchema>;

/** Who holds a tag: a person or a service account. */
export const TagHolderSchema = z.object({
  principal_id: z.string(),
  name: z.string(),
  type: z.enum(["user", "service", "any"]),
  home_unit_id: z.string().nullable(),
  since: z.string(),
});
export type TagHolder = z.infer<typeof TagHolderSchema>;
export const TagHolderListSchema = listSchema(TagHolderSchema);

/**
 * Tags held or carried, with whether the reader may change them and the tags they may choose from: the active tags
 * they hold, or every active tag when they hold none (the escalation rule for people, rule 5 for records).
 */
const HeldTagsShape = {
  tags: z.array(TagSummarySchema),
  may_change: z.boolean(),
  choices: z.array(TagSummarySchema),
};

export const HolderTagsSchema = z.object({ principal_id: z.string(), ...HeldTagsShape });
export type HolderTags = z.infer<typeof HolderTagsSchema>;

export const RecordTagsSchema = z.object({ record_type: z.string(), record_id: z.string(), ...HeldTagsShape });
export type RecordTags = z.infer<typeof RecordTagsSchema>;

/** What a tags control shows and changes, the same for a person and a record. */
export type HeldTags = z.infer<z.ZodObject<typeof HeldTagsShape>>;

const tags = "/api/v1/visibility-tags";

export const visibilityTagsQueryKey = ["visibility", "tags"] as const;

export const fetchVisibilityTags = (signal?: AbortSignal) =>
  request(`${tags}?page_size=200`, { schema: VisibilityTagListSchema, ...(signal ? { signal } : {}) });

export const createVisibilityTag = (body: { code: string; name: string; description: string | null }) =>
  request(tags, { method: "POST", body, schema: VisibilityTagSchema });

export const changeVisibilityTag = (id: string, body: { name: string; description: string | null }) =>
  request(`${tags}/${encodeURIComponent(id)}`, { method: "PUT", body, schema: VisibilityTagSchema });

export const retireVisibilityTag = (id: string) =>
  request(`${tags}/${encodeURIComponent(id)}/retire`, { method: "POST", schema: VisibilityTagSchema });

export const reactivateVisibilityTag = (id: string) =>
  request(`${tags}/${encodeURIComponent(id)}/reactivate`, { method: "POST", schema: VisibilityTagSchema });

export const fetchTagHolders = (id: string, query: string, signal?: AbortSignal) =>
  request(`${tags}/${encodeURIComponent(id)}/holders${query ? `?${query}` : ""}`, {
    schema: TagHolderListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchHolderTags = (principalId: string, signal?: AbortSignal) =>
  request(`/api/v1/users/${encodeURIComponent(principalId)}/visibility-tags`, {
    schema: HolderTagsSchema,
    ...(signal ? { signal } : {}),
  });

export const setHolderTags = (principalId: string, codes: string[]) =>
  request(`/api/v1/users/${encodeURIComponent(principalId)}/visibility-tags`, {
    method: "PUT",
    body: { codes },
    schema: HolderTagsSchema,
  });

export const fetchRecordTags = (recordType: string, id: string, signal?: AbortSignal) =>
  request(`/api/v1/record-tags/${encodeURIComponent(recordType)}/${encodeURIComponent(id)}`, {
    schema: RecordTagsSchema,
    ...(signal ? { signal } : {}),
  });

export const setRecordTags = (recordType: string, id: string, codes: string[]) =>
  request(`/api/v1/record-tags/${encodeURIComponent(recordType)}/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: { codes },
    schema: RecordTagsSchema,
  });
