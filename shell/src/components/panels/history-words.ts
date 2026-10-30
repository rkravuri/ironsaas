/**
 * A field's value as the history keeps it, for people: a list of words (a record's visibility tags, a delegation's
 * duties) is kept as its JSON, and reads as the words joined, or "none" when empty; anything else stays as it is.
 */
export function valueWords(text: string, emptyList: string): string {
  if (!text.startsWith("[")) {
    return text;
  }
  try {
    const parsed: unknown = JSON.parse(text);
    return Array.isArray(parsed) && parsed.every((item) => typeof item === "string")
      ? parsed.length === 0
        ? emptyList
        : parsed.join(", ")
      : text;
  } catch {
    return text;
  }
}
