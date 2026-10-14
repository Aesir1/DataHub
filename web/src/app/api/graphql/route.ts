import { auth } from "@/auth";

/**
 * Backend-for-frontend: the browser posts GraphQL here, the server attaches the Keycloak access token.
 * Wrapped in auth() because a bare auth() call drops the Set-Cookie of a refreshed session: the next
 * request then replays the old single-use refresh token and Keycloak revokes the whole SSO session
 * ("Maximum allowed refresh token reuse exceeded"). The wrapper copies the rotated cookie onto the response.
 */
export const POST = auth(async (req) => {
  const session = req.auth;
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
});
