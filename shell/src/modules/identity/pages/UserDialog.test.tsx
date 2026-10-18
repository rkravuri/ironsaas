import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithProviders, stubFetch } from "@/test/render";
import { UserDialog } from "./UserDialog";

const user = {
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "ada@acme.localhost",
  display_name: "Ada Lovelace",
  type: "user",
  status: "active",
  email: "ada@example.com",
  last_login_at: null,
  created_at: "2026-10-01T09:00:00Z",
  updated_at: "2026-10-02T09:00:00Z",
  version: 3,
};

describe("the user dialog", () => {
  it("shows the user on the record header with the status as a word", async () => {
    stubFetch({});
    renderWithProviders(<UserDialog user={user} onClose={() => undefined} />);
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Ada Lovelace");
    expect(dialog).toHaveTextContent("ada@acme.localhost");
    expect(dialog).toHaveTextContent("Active");
    expect(dialog).toHaveTextContent("ada@example.com");
    expect(dialog).toHaveTextContent("Never");
  });

  it("stays closed without a user", () => {
    stubFetch({});
    renderWithProviders(<UserDialog user={undefined} onClose={() => undefined} />);
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
