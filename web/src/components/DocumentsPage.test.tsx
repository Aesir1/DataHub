import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { graphql, http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { server } from "@/test/server";
import { checkFile, DocumentsPage } from "./DocumentsPage";

const doc = (status: "PENDING" | "AVAILABLE") => ({
  id: "d1",
  fileName: "notes.txt",
  contentType: "text/plain",
  sizeBytes: 5,
  status,
  createdAtUtc: "2026-09-25T10:00:00Z",
  downloadUrl: status === "AVAILABLE" ? "http://minio.test/documents/get" : null,
});

describe("DocumentsPage", () => {
  it("requests a presigned URL, PUTs the file there, confirms, and lists it as available", async () => {
    let stored: string | null = null;
    let confirmed = false;
    server.use(
      graphql.query("Documents", () =>
        HttpResponse.json({ data: { documents: { nodes: confirmed ? [doc("AVAILABLE")] : [] } } }),
      ),
      graphql.mutation("RequestDocumentUpload", ({ variables }) => {
        expect(variables.input).toEqual({ fileName: "notes.txt", contentType: "text/plain", sizeBytes: 5 });
        return HttpResponse.json({
          data: {
            requestDocumentUpload: {
              documentUploadPayload: {
                uploadUrl: "http://minio.test/documents/put?X-Amz-Signature=x",
                document: { id: "d1" },
              },
              errors: null,
            },
          },
        });
      }),
      http.put("http://minio.test/documents/put", async ({ request }) => {
        expect(request.headers.get("content-type")).toBe("text/plain");
        stored = await request.text();
        return new HttpResponse(null, { status: 200 });
      }),
      graphql.mutation("ConfirmDocumentUpload", () => {
        confirmed = true;
        return HttpResponse.json({
          data: { confirmDocumentUpload: { document: { id: "d1", status: "AVAILABLE" }, errors: null } },
        });
      }),
    );
    const { container } = render(<DocumentsPage />);
    await screen.findByText("No documents yet.");

    const input = container.querySelector<HTMLInputElement>('input[type="file"]')!;
    fireEvent.change(input, {
      target: { files: [new File(["hello"], "notes.txt", { type: "text/plain" })] },
    });

    const link = await screen.findByRole("link", { name: "notes.txt" });
    expect(link.getAttribute("href")).toBe("http://minio.test/documents/get");
    expect(stored).toBe("hello");
    expect(screen.getByText("Available")).toBeTruthy();
  });

  it("rejects disallowed types before contacting the Api", async () => {
    server.use(graphql.query("Documents", () => HttpResponse.json({ data: { documents: { nodes: [] } } })));
    const { container } = render(<DocumentsPage />);
    await screen.findByText("No documents yet.");

    fireEvent.change(container.querySelector('input[type="file"]')!, {
      target: { files: [new File(["x"], "virus.exe", { type: "application/x-msdownload" })] },
    });

    expect((await screen.findByRole("alert")).textContent).toMatch(/not allowed/);
  });

  it("shows Api errors from rename", async () => {
    server.use(
      graphql.query("Documents", () =>
        HttpResponse.json({ data: { documents: { nodes: [doc("AVAILABLE")] } } }),
      ),
      graphql.mutation("RenameDocument", () =>
        HttpResponse.json({
          data: {
            renameDocument: {
              document: null,
              errors: [
                {
                  __typename: "ValidationError",
                  message: "Validation failed.",
                  fields: [{ field: "fileName", messages: ["Too long."] }],
                },
              ],
            },
          },
        }),
      ),
    );
    render(<DocumentsPage />);

    fireEvent.click(await screen.findByRole("button", { name: "Rename" }));
    fireEvent.submit(screen.getByRole("form", { name: "Rename notes.txt" }));

    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("Too long."));
  });

  it("checks size limits", () => {
    expect(checkFile(new File([], "empty.txt", { type: "text/plain" }))).toMatch(/size/);
    expect(checkFile(new File(["x"], "a.pdf", { type: "application/pdf" }))).toBeNull();
  });
});
