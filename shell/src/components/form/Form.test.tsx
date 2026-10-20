import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TextInput } from "@mantine/core";
import { describe, expect, it, vi } from "vitest";
import { z } from "zod";
import { ApiError, ProblemSchema } from "@/api/client";
import { fill, renderWithProviders, stubFetch } from "@/test/render";
import { Form } from "./Form";
import { useZodForm } from "./useZodForm";

const schema = z.object({
  displayName: z.string().min(1, "Enter a display name."),
  email: z.string().email("Enter an email address."),
});
type Values = z.infer<typeof schema>;

function Sample({ onSubmit }: { onSubmit: (values: Values) => Promise<void> }) {
  const form = useZodForm({ schema, initialValues: { displayName: "", email: "" } });
  return (
    <Form form={form} onSubmit={onSubmit} submitLabel="Save" secondary={{ label: "Cancel", onClick: () => undefined }}>
      <TextInput label="Display name" {...form.getInputProps("displayName")} />
      <TextInput label="Email" {...form.getInputProps("email")} />
    </Form>
  );
}

describe("the form", () => {
  it("validates a field after its first blur and keeps the primary action last [ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    renderWithProviders(<Sample onSubmit={() => Promise.resolve()} />);
    await user.click(screen.getByLabelText("Display name"));
    await user.tab();
    expect(await screen.findByText("Enter a display name.")).toBeInTheDocument();
    const buttons = screen.getAllByRole("button");
    expect(buttons.at(-1)).toHaveTextContent("Save");
    expect(buttons.map((button) => button.textContent)).toEqual(["Cancel", "Save"]);
  });

  it("puts the API's field problems on the fields and the rest at the top with the correlation id [TF-171 AC3, ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    const problem = ProblemSchema.parse({
      status: 400,
      error_code: "api.validation.failed",
      correlation_id: "abc123",
      errors: [
        { field: "email", code: "email", message: "This email is already used." },
        { field: "tenant", code: "not_found", message: "The tenant does not exist." },
      ],
    });
    const onSubmit = vi.fn(() => Promise.reject(new ApiError(400, problem, undefined)));
    renderWithProviders(<Sample onSubmit={onSubmit} />);
    await fill(user, screen.getByLabelText("Display name"), "Ada");
    await fill(user, screen.getByLabelText("Email"), "ada@example.com");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledOnce());
    expect(await screen.findByText("This email is already used.")).toBeInTheDocument();
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("The tenant does not exist.");
    expect(alert).toHaveTextContent("abc123");
  });
});
