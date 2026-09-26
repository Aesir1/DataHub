import { auth } from "@/auth";

/**
 * Backend-for-frontend: the browser posts GraphQL here, the server attaches the Keycloak access token.
 * Route handlers can write cookies, so a token refreshed by auth() is persisted.
 */
export async function POST(req: Request) {
  const session = await auth();
  if (!session?.accessToken || session.error) {
    return Response.json({ errors: [{ message: "Not authenticated" }] }, { status: 401 });
  }

  const res = await fetch(`${process.env.API_URL}/graphql`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${session.accessToken}` },
    body: await req.text(),
    cache: "no-store",
  });
  return new Response(res.body, { status: res.status, headers: { "content-type": "application/json" } });
}
