import { z } from "zod";
import { listSchema, request } from "./client";

/** The record type the kernel's sharing and tags name sample records by (ADR-0057). */
export const sampleRecordType = "sample_record";

/** A sample record (ADR-0057): a name, its unit, its sharing and its tags; only units marked for testing have them. */
export const SampleRecordSchema = z.object({
  id: z.string(),
  name: z.string(),
  owning_unit_id: z.string(),
  sharing_mode: z.enum(["private", "shared", "tenant"]),
  visibility_tags: z.array(z.string()),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type SampleRecord = z.infer<typeof SampleRecordSchema>;
export const SampleRecordListSchema = listSchema(SampleRecordSchema);

const records = "/api/v1/sample-records";

export const fetchSampleRecords = (query: string, signal?: AbortSignal) =>
  request(`${records}${query ? `?${query}` : ""}`, { schema: SampleRecordListSchema, ...(signal ? { signal } : {}) });

export const fetchSampleRecord = (id: string, signal?: AbortSignal) =>
  request(`${records}/${encodeURIComponent(id)}`, { schema: SampleRecordSchema, ...(signal ? { signal } : {}) });

export const createSampleRecord = (name: string) =>
  request(records, { method: "POST", body: { name }, schema: SampleRecordSchema });
