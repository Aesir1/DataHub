"use client";

import { useCallback, useEffect, useState } from "react";
import { graphql } from "@/gql";
import type { DocumentsQuery } from "@/gql/graphql";
import { formErrors } from "@/lib/errors";
import { execute } from "@/lib/graphql";
import { uploadWithProgress } from "@/lib/upload";

const DocumentsDocument = graphql(`
  query Documents {
    documents(first: 100) {
      nodes {
        id
        fileName
        contentType
        sizeBytes
        status
        createdAtUtc
        downloadUrl
      }
    }
  }
`);

const RequestUploadDocument = graphql(`
  mutation RequestDocumentUpload($input: RequestDocumentUploadInput!) {
    requestDocumentUpload(input: $input) {
      documentUploadPayload {
        uploadUrl
        document {
          id
        }
      }
      errors {
        __typename
        ... on Error {
          message
        }
        ... on ValidationError {
          fields {
            field
            messages
          }
        }
      }
    }
  }
`);

const ConfirmUploadDocument = graphql(`
  mutation ConfirmDocumentUpload($input: ConfirmDocumentUploadInput!) {
    confirmDocumentUpload(input: $input) {
      document {
        id
        status
      }
      errors {
        __typename
        ... on Error {
          message
        }
      }
    }
  }
`);

const RenameDocumentDocument = graphql(`
  mutation RenameDocument($input: RenameDocumentInput!) {
    renameDocument(input: $input) {
      document {
        id
      }
      errors {
        __typename
        ... on Error {
          message
        }
        ... on ValidationError {
          fields {
            field
            messages
          }
        }
      }
    }
  }
`);

const ReplaceContentDocument = graphql(`
  mutation ReplaceDocumentContent($input: ReplaceDocumentContentInput!) {
    replaceDocumentContent(input: $input) {
      documentUploadPayload {
        uploadUrl
      }
      errors {
        __typename
        ... on Error {
          message
        }
        ... on ValidationError {
          fields {
            field
            messages
          }
        }
      }
    }
  }
`);

const DeleteDocumentDocument = graphql(`
  mutation DeleteDocument($input: DeleteDocumentInput!) {
    deleteDocument(input: $input) {
      errors {
        __typename
        ... on Error {
          message
        }
      }
    }
  }
`);

type Doc = NonNullable<NonNullable<DocumentsQuery["documents"]>["nodes"]>[number];

export const MAX_BYTES = 25 * 1024 * 1024;
export const ALLOWED_TYPES = [
  "application/pdf",
  "image/png",
  "image/jpeg",
  "text/plain",
  "text/csv",
  "application/json",
];

const button = "rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface disabled:opacity-50";
const size = (bytes: number) =>
  bytes < 1024
    ? `${bytes} B`
    : bytes < 1024 ** 2
      ? `${(bytes / 1024).toFixed(1)} KB`
      : `${(bytes / 1024 ** 2).toFixed(1)} MB`;

/** Client-side pre-check; the Api enforces the same limits when issuing the URL (ST-3). */
export function checkFile(file: File): string | null {
  if (!ALLOWED_TYPES.includes(file.type))
    return `${file.name}: type ${file.type || "unknown"} is not allowed.`;
  if (file.size === 0 || file.size > MAX_BYTES) return `${file.name}: size must be between 1 byte and 25 MB.`;
  return null;
}

function messageOf(errors: Parameters<typeof formErrors>[0]) {
  const problems = formErrors(errors);
  return problems ? [problems.message, ...Object.values(problems.fields)].filter(Boolean).join(" ") : null;
}

/** ST-6: list, upload with progress (straight to object storage via presigned PUT), rename, replace, delete. */
export function DocumentsPage() {
  const [docs, setDocs] = useState<Doc[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ name: string; percent: number } | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);

  const reload = useCallback(
    () =>
      execute(DocumentsDocument).then(
        (d) => setDocs(d.documents?.nodes ?? []),
        (e: Error) => setError(e.message),
      ),
    [],
  );
  useEffect(() => {
    void reload();
  }, [reload]);

  async function send(file: File, getUrl: () => Promise<{ url: string; id: string } | string>) {
    const invalid = checkFile(file);
    if (invalid) return setError(invalid);
    setError(null);
    try {
      const target = await getUrl();
      if (typeof target === "string") return setError(target);
      setProgress({ name: file.name, percent: 0 });
      await uploadWithProgress(target.url, file, (percent) => setProgress({ name: file.name, percent }));
      const { confirmDocumentUpload } = await execute(ConfirmUploadDocument, { input: { id: target.id } });
      setError(messageOf(confirmDocumentUpload.errors));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setProgress(null);
      await reload();
    }
  }

  const upload = (file: File) =>
    send(file, async () => {
      const { requestDocumentUpload } = await execute(RequestUploadDocument, {
        input: { fileName: file.name, contentType: file.type, sizeBytes: file.size },
      });
      const payload = requestDocumentUpload.documentUploadPayload;
      return payload
        ? { url: payload.uploadUrl, id: payload.document.id }
        : (messageOf(requestDocumentUpload.errors) ?? "Upload refused.");
    });

  const replace = (doc: Doc, file: File) =>
    send(file, async () => {
      const { replaceDocumentContent } = await execute(ReplaceContentDocument, {
        input: { id: doc.id, contentType: file.type, sizeBytes: file.size },
      });
      const payload = replaceDocumentContent.documentUploadPayload;
      return payload
        ? { url: payload.uploadUrl, id: doc.id }
        : (messageOf(replaceDocumentContent.errors) ?? "Replace refused.");
    });

  async function rename(doc: Doc, fileName: string) {
    const { renameDocument } = await execute(RenameDocumentDocument, { input: { id: doc.id, fileName } });
    const message = messageOf(renameDocument.errors);
    setError(message);
    if (!message) setRenaming(null);
    await reload();
  }

  async function remove(doc: Doc) {
    if (!confirm(`Delete ${doc.fileName}?`)) return;
    const { deleteDocument } = await execute(DeleteDocumentDocument, { input: { id: doc.id } });
    setError(messageOf(deleteDocument.errors));
    await reload();
  }

  return (
    <main className="space-y-6 p-4">
      <h1 className="text-xl font-semibold">Documents</h1>
      <div className="flex flex-wrap items-center gap-3">
        <label className="cursor-pointer rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-on-primary hover:opacity-90">
          Upload file
          <input
            type="file"
            className="sr-only"
            accept={ALLOWED_TYPES.join(",")}
            disabled={!!progress}
            onChange={(e) => {
              const file = e.target.files?.[0];
              e.target.value = "";
              if (file) void upload(file);
            }}
          />
        </label>
        <span className="text-xs text-muted">PDF, PNG, JPEG, text, CSV or JSON, up to 25 MB.</span>
      </div>
      {progress && (
        <div>
          <label htmlFor="upload-progress" className="text-sm">
            Uploading {progress.name}… {progress.percent}%
          </label>
          <progress
            id="upload-progress"
            className="block w-full max-w-md"
            max={100}
            value={progress.percent}
          />
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-error">
          {error}
        </p>
      )}
      {docs?.length === 0 && <p className="text-muted">No documents yet.</p>}
      {!!docs?.length && (
        <table className="w-full max-w-5xl text-sm">
          <thead>
            <tr className="border-b border-border text-left text-muted">
              <th scope="col" className="py-2 font-medium">
                Name
              </th>
              <th scope="col" className="py-2 font-medium">
                Type
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Size
              </th>
              <th scope="col" className="py-2 font-medium">
                Status
              </th>
              <th scope="col" className="py-2">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {docs.map((d) => (
              <tr key={d.id} className="border-b border-border">
                <td className="py-2">
                  {renaming === d.id ? (
                    <form
                      aria-label={`Rename ${d.fileName}`}
                      className="flex gap-2"
                      action={(form) => rename(d, String(form.get("fileName")))}
                    >
                      <input
                        name="fileName"
                        defaultValue={d.fileName}
                        aria-label="New name"
                        className="rounded-md border border-border bg-background px-2 py-1"
                      />
                      <button type="submit" className={button}>
                        Save
                      </button>
                    </form>
                  ) : d.downloadUrl ? (
                    <a
                      href={d.downloadUrl}
                      className="text-primary underline"
                      target="_blank"
                      rel="noreferrer"
                    >
                      {d.fileName}
                    </a>
                  ) : (
                    d.fileName
                  )}
                </td>
                <td className="py-2 text-muted">{d.contentType}</td>
                <td className="py-2 text-right tabular-nums">{size(d.sizeBytes)}</td>
                <td className="py-2">{d.status === "AVAILABLE" ? "Available" : "Pending"}</td>
                <td className="space-x-2 py-2 text-right whitespace-nowrap">
                  <button
                    type="button"
                    className={button}
                    onClick={() => setRenaming(renaming === d.id ? null : d.id)}
                  >
                    Rename
                  </button>
                  <label className={`${button} cursor-pointer`}>
                    Replace
                    <input
                      type="file"
                      className="sr-only"
                      aria-label={`Replace ${d.fileName}`}
                      accept={ALLOWED_TYPES.join(",")}
                      onChange={(e) => {
                        const file = e.target.files?.[0];
                        e.target.value = "";
                        if (file) void replace(d, file);
                      }}
                    />
                  </label>
                  <button type="button" className={button} onClick={() => void remove(d)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
