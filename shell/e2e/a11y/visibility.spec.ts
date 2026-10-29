import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { administrator, envelope, me, record, stubAccess, units } from "./stubs";

const schemes = ["light", "dark"] as const;
const wcag21aa = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolation(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcag21aa).analyze();
  expect(
    results.violations.map(
      (violation) =>
        `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.map(String).join(" ")).join("; ")}`,
    ),
  ).toEqual([]);
}

const tag = (
  n: number,
  code: string,
  name: string,
  status: "active" | "retired",
  holders: number,
  description: string | null,
) => ({
  id: `01a19000-0000-7000-8000-${String(n).padStart(12, "0")}`,
  code,
  name,
  description,
  status,
  holders,
  updated_at: "2026-10-29T09:00:00Z",
  version: 1,
});
const tags = [
  tag(1, "APAC", "Asia Pacific", "active", 3, "Our region in Asia and the Pacific"),
  tag(2, "EMEA", "Europe, Middle East and Africa", "active", 0, null),
  tag(3, "PROJECT-7", "Project Seven", "retired", 1, "The closed programme"),
];
const summary = (row: (typeof tags)[number]) => ({ code: row.code, name: row.name, active: row.status === "active" });
const sample = {
  id: "01a19100-0000-7000-8000-000000000001",
  name: "North sample",
  owning_unit_id: units[4].id,
  sharing_mode: "private",
  visibility_tags: ["APAC"],
  created_at: "2026-10-29T09:00:00Z",
  updated_at: "2026-10-29T09:30:00Z",
  version: 2,
};
const unit = (row: (typeof units)[number]) => ({ id: row.id, name: row.name, code: row.code });

/** The console's stubs, an administrator who also manages visibility tags and sample records, and their answers. */
async function stubVisibility(page: Page): Promise<void> {
  await stubAccess(page);
  const permissions = [
    ...administrator,
    "visibility.tags.read",
    "visibility.tags.manage",
    "visibility.assignments.manage",
    "samples.records.read",
    "samples.records.manage",
  ];
  await page.route("**/api/v1/me", (route) =>
    route.fulfill({ json: { ...me, permissions, permissions_anywhere: permissions, home_unit: unit(units[0]) } }),
  );
  await page.route("**/api/v1/visibility-tags?**", (route) =>
    route.fulfill({ json: envelope(route.request().url(), tags) }),
  );
  await page.route("**/api/v1/visibility-tags/*/holders?**", (route) =>
    route.fulfill({
      json: envelope(route.request().url(), [
        {
          principal_id: record.id,
          name: record.display_name,
          type: "user",
          home_unit_id: units[3].id,
          since: "2026-10-28T09:00:00Z",
        },
      ]),
    }),
  );
  const held = { tags: [summary(tags[0])], may_change: true, choices: [summary(tags[0]), summary(tags[1])] };
  await page.route(`**/api/v1/users/${record.id}/visibility-tags`, (route) =>
    route.fulfill({ json: { principal_id: record.id, ...held } }),
  );
  await page.route(`**/api/v1/sample-records/${sample.id}`, (route) => route.fulfill({ json: sample }));
  await page.route(`**/api/v1/record-tags/sample_record/${sample.id}`, (route) =>
    route.fulfill({
      json: {
        record_type: "sample_record",
        record_id: sample.id,
        ...held,
        tags: [summary(tags[0]), summary(tags[2])],
      },
    }),
  );
  await page.route(`**/api/v1/record-sharing/sample_record/${sample.id}`, (route) =>
    route.fulfill({
      json: {
        record_type: "sample_record",
        record_id: sample.id,
        owning_unit: unit(units[4]),
        mode: "private",
        units: [],
        may_change: false,
        choices: [],
      },
    }),
  );
}

for (const scheme of schemes) {
  test(`content visibility in the ${scheme} scheme: the tags and who holds them, a person's tags and a record's, each dialog by keyboard, and no WCAG 2.1 AA violation [TF-172 AC1, ADR-0024, ADR-0057]`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await stubVisibility(page);
    await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });

    // The console's page: each tag with its status in words, and who holds the one chosen.
    await page.goto("/console/visibility");
    await expect(page.getByRole("heading", { level: 2, name: "Content visibility" })).toBeVisible();
    const table = page.getByRole("table", { name: "The visibility tags of this organisation" });
    await expect(table.getByRole("row")).toHaveCount(4);
    await expect(table).toContainText("Retired");
    await page.screenshot({ path: `test-results/a11y/visibility-page-${scheme}.png`, fullPage: true });
    await expectNoViolation(page);

    await page.getByRole("button", { name: "New tag" }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "New visibility tag" });
    await expect(dialog.getByRole("textbox", { name: "Code" })).toBeFocused();
    await page.screenshot({ path: `test-results/a11y/visibility-new-tag-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();

    // A person's Visibility tab, and the change of their tags.
    await page.goto(`/users/${record.id}`);
    await page.getByRole("tab", { name: "Visibility" }).click();
    await expect(page.getByRole("list", { name: "Visibility tags" })).toContainText("Asia Pacific (APAC)");
    await page.screenshot({ path: `test-results/a11y/visibility-user-tab-${scheme}.png` });
    await expectNoViolation(page);
    await page.getByRole("button", { name: "Change tags" }).focus();
    await page.keyboard.press("Enter");
    const personDialog = page.getByRole("dialog", { name: `Change the visibility tags of ${record.display_name}` });
    await expect(personDialog.getByRole("checkbox", { name: "Asia Pacific (APAC)" })).toBeFocused();
    await expect(
      personDialog.getByRole("checkbox", { name: "Europe, Middle East and Africa (EMEA)" }),
    ).not.toBeChecked();
    await page.screenshot({ path: `test-results/a11y/visibility-user-dialog-${scheme}.png` });
    await expectNoViolation(page);
    await page.keyboard.press("Escape");

    // A record's tags under its header, a retired one said so, and their change.
    await page.goto(`/samples/${sample.id}`);
    await expect(page.getByRole("heading", { name: sample.name })).toBeVisible();
    await expect(page.getByRole("list", { name: "Visibility tags" })).toContainText(
      "Project Seven (PROJECT-7), retired",
    );
    await page.screenshot({ path: `test-results/a11y/visibility-record-${scheme}.png` });
    await expectNoViolation(page);
    await page.getByRole("button", { name: "Change tags" }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("dialog", { name: "Change the record's visibility tags" })).toBeVisible();
    await page.screenshot({ path: `test-results/a11y/visibility-record-dialog-${scheme}.png` });
    await expectNoViolation(page);
  });
}
