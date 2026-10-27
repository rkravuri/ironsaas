import { z } from "zod";

/**
 * A rich-text field as the API sends it (ADR-0053): HTML the server cleaned by its allow-list when it was written
 * (RichText, in the kernel's security library). The brand means only an API binding's schema makes one, so a page cannot
 * hand RichTextView a string it built itself.
 */
export const richTextSchema = z.string().brand<"RichText">();

export type RichTextHtml = z.infer<typeof richTextSchema>;
