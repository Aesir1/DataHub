import { auth } from "@/auth";
import type { Session } from "next-auth";

/**
 * Document bytes go browser → here → Api → S3 (and back); S3 itself is never reachable from outside.
 * Bodies are streamed, not buffered. The Api checks ownership, content type and the 25 MB limit.
 * Handlers are wrapped in auth() so a refreshed session cookie reaches the browser (see the graphql route).
 */
async function forward(session: Session | null, req: Request, id: string): Promise<Response> {
  if (!session?.accessToken || session.error) {
    return new Response("Not authenticated", { status: 401 });
  }

  const headers = new Headers({ authorization: `Bearer ${session.accessToken}` });
  for (const name of ["content-type", "content-length"]) {
    const value = req.headers.get(name);
    if (value) headers.set(name, value);
  }

  const res = await fetch(`${process.env.API_URL}/documents/${encodeURIComponent(id)}/content`, {
    method: req.method,
    headers,
    body: req.method === "PUT" ? req.body : undefined,
    // Required by Node's fetch for a streamed request body.
    duplex: "half",
    cache: "no-store",
  } as RequestInit);
  return new Response(res.body, { status: res.status, headers: copy(res.headers) });
}

function copy(from: Headers): Headers {
  const to = new Headers({ "cache-control": "private, no-store" });
  for (const name of ["content-type", "content-length", "content-disposition"]) {
    const value = from.get(name);
    if (value) to.set(name, value);
  }
  return to;
}

export const GET = auth(async (req, { params }) => {
  return forward(req.auth, req, ((await params) as { id: string }).id);
});

export const PUT = auth(async (req, { params }) => {
  return forward(req.auth, req, ((await params) as { id: string }).id);
});
