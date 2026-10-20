import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { fill, renderWithProviders, stubFetch } from "@/test/render";
import {
  ApprovalPanel,
  AttachmentPanel,
  CommentPanel,
  ConfirmDialog,
  EmptyState,
  FormDialog,
  HistoryPanel,
  LoadingState,
} from "./index";

describe("the empty and loading states", () => {
  it("carry their drawing beside the words [ADR-0026, ADR-0027]", () => {
    stubFetch({});
    renderWithProviders(
      <>
        <EmptyState
          drawing="nothingFound"
          title="Nothing matches"
          text="Change the filter."
          action={<button type="button">Clear</button>}
        />
        <LoadingState text="Loading the records" />
      </>,
    );
    expect(document.querySelector("svg[data-drawing='nothingFound']")).toHaveAttribute("aria-hidden", "true");
    expect(screen.getByRole("heading", { name: "Nothing matches" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear" })).toBeInTheDocument();
    expect(document.querySelector("svg[data-drawing='loading']")).not.toBeNull();
    expect(screen.getByText("Loading the records")).toBeInTheDocument();
  });
});

describe("the dialogs", () => {
  it("ask a question with one primary answer and a way out [ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    renderWithProviders(
      <ConfirmDialog
        opened
        title="Delete the unit?"
        message="Its records move to the parent."
        confirmLabel="Delete"
        danger
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    );
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Its records move to the parent.");
    await user.click(screen.getByRole("button", { name: "Delete" }));
    expect(onConfirm).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("frame a form without adding a second primary [ADR-0027]", async () => {
    stubFetch({});
    renderWithProviders(
      <FormDialog opened title="Rename" onClose={() => undefined}>
        <form>
          <button type="submit">Save</button>
        </form>
      </FormDialog>,
    );
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Rename");
    expect(screen.getAllByRole("button", { name: "Save" })).toHaveLength(1);
  });
});

describe("the attachment panel", () => {
  it("lists the files, downloads one, uploads one and removes one [ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    const onUpload = vi.fn(() => Promise.resolve());
    const onDownload = vi.fn();
    const onDelete = vi.fn(() => Promise.resolve());
    renderWithProviders(
      <AttachmentPanel
        attachments={[
          { id: "a", name: "contract.pdf", size: 24_576, uploadedAt: "2026-10-18T09:00:00Z", uploadedBy: "Ada" },
        ]}
        onUpload={onUpload}
        onDownload={onDownload}
        onDelete={onDelete}
      />,
    );
    expect(screen.getByText("24 KB")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Download contract.pdf" }));
    expect(onDownload).toHaveBeenCalledWith(expect.objectContaining({ id: "a" }));
    await user.click(screen.getByRole("button", { name: "Remove contract.pdf" }));
    expect(onDelete).toHaveBeenCalledOnce();
    const input = document.querySelector("input[type='file']");
    expect(input).not.toBeNull();
    await user.upload(input as HTMLInputElement, new File(["x"], "notes.txt", { type: "text/plain" }));
    expect(onUpload).toHaveBeenCalledOnce();
  });

  it("says when there is nothing and stays read-only without the callbacks [ADR-0027]", () => {
    stubFetch({});
    renderWithProviders(<AttachmentPanel attachments={[]} />);
    expect(screen.getByText("No attachments yet.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Upload a file" })).toBeDisabled();
  });
});

describe("the history, approval and comment panels", () => {
  it("show the events, the steps with their state in words, and the comments with a composer [TF-172 AC3, ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    const onAdd = vi.fn(() => Promise.resolve());
    renderWithProviders(
      <>
        <HistoryPanel
          events={[
            { id: "e1", at: "2026-10-18T09:00:00Z", actor: "Ada", action: "Created", summary: "From the import." },
          ]}
        />
        <ApprovalPanel
          steps={[
            { id: "s1", name: "Manager", approver: "Grace", state: "approved" },
            { id: "s2", name: "Finance", approver: "Linus", state: "pending" },
          ]}
        />
        <CommentPanel
          comments={[{ id: "c1", author: "Ada", at: "2026-10-18T09:00:00Z", text: "Looks right." }]}
          onAdd={onAdd}
        />
      </>,
    );
    expect(screen.getByText("Created")).toBeInTheDocument();
    expect(screen.getByText("Approved")).toBeInTheDocument();
    expect(screen.getByText("Pending")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByText("Looks right.")).toBeInTheDocument();
    await fill(user, screen.getByLabelText("Your comment"), "Agreed.");
    await user.click(screen.getByRole("button", { name: "Add the comment" }));
    expect(onAdd).toHaveBeenCalledWith("Agreed.");
  });
});
