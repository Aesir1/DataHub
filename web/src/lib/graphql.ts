import type { TypedDocumentString } from "@/gql/graphql";

export class GraphQlError extends Error {}

/** Typed GraphQL call through the /api/graphql proxy. Only generated documents are accepted. */
export async function execute<TResult, TVariables>(
  document: TypedDocumentString<TResult, TVariables>,
  ...[variables]: TVariables extends Record<string, never> ? [] : [TVariables]
): Promise<TResult> {
  const res = await fetch("/api/graphql", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ query: document.toString(), variables }),
  });
  if (res.status === 401) {
    // Session is gone; reloading lets proxy.ts send the user to the start screen.
    window.location.reload();
    throw new GraphQlError("Session expired");
  }

  const body = (await res.json()) as { data?: TResult; errors?: { message: string }[] };
  if (body.errors?.length) throw new GraphQlError(body.errors.map((e) => e.message).join("; "));
  if (!body.data) throw new GraphQlError(`Request failed (${res.status})`);
  return body.data;
}
