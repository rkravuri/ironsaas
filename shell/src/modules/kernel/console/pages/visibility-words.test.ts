import { describe, expect, it } from "vitest";
import i18n from "@/i18n";
import type { AccessExplanation, ExplainedCheck, ReviewItem } from "@/api/authorization";
import { valueWords } from "@/components/panels/history-words";
import { checkWords } from "./explain-words";
import { heldTagsWords, reviewedWords } from "./review-words";

const t = i18n.t.bind(i18n);

const explanation = { principal: { name: "Linus Pauling" }, acting_for: null } as unknown as AccessExplanation;

const tagCheck = (reason: string, facts: Record<string, string>): ExplainedCheck => ({
  check: "visibility_tags",
  result: reason === "no_shared_tag" ? "failed" : "passed",
  reason,
  status: reason === "no_shared_tag" ? 404 : null,
  error_code: reason === "no_shared_tag" ? "data.record.not_found" : null,
  message: "The API's own words.",
  facts,
  granted_by: [],
});

describe("visibility tags in words", () => {
  it("names the tag check of an explanation with the tags compared [TF-241 AC2, ADR-0039]", () => {
    expect(checkWords(t, tagCheck("no_shared_tag", { record_tags: "LATAM", held_tags: "APAC" }), explanation)).toBe(
      "The record carries LATAM; Linus Pauling holds APAC. They share no tag, so the system answers as if the record did not exist.",
    );
    expect(
      checkWords(
        t,
        tagCheck("shared_tag", { record_tags: "APAC, LATAM", held_tags: "APAC", shared_tags: "APAC" }),
        explanation,
      ),
    ).toBe("The record carries APAC, LATAM; Linus Pauling holds APAC. They share APAC, so Linus Pauling sees it.");
    expect(t("explain.checks.visibility_tags")).toBe("The visibility tags");
  });

  it("lists the tags each person of a review holds, and names a holding's tag [TF-241 AC7]", () => {
    const roleItem = {
      role_name: "Buyer",
      tag: null,
      held_tags: [
        { code: "APAC", name: "Asia Pacific" },
        { code: "EMEA", name: "Europe" },
      ],
    } as unknown as ReviewItem;
    const tagItem = {
      role_name: null,
      tag: { code: "LATAM", name: "Latin America" },
      held_tags: [],
    } as unknown as ReviewItem;

    expect(reviewedWords(roleItem, t)).toBe("Buyer");
    expect(heldTagsWords(roleItem, t)).toBe("APAC, EMEA");
    expect(reviewedWords(tagItem, t)).toBe("the tag Latin America (LATAM)");
    expect(heldTagsWords(tagItem, t)).toBe("None");
  });

  it("reads a list the history keeps as its JSON as words, an empty list as none, and leaves other values as they are [ADR-0057]", () => {
    expect(valueWords('["APAC","LATAM"]', "None")).toBe("APAC, LATAM");
    expect(valueWords("[]", "None")).toBe("None");
    expect(valueWords("Asia Pacific", "None")).toBe("Asia Pacific");
    expect(valueWords("[not json", "None")).toBe("[not json");
  });
});
