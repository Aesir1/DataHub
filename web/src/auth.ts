import { redirect } from "next/navigation";
import NextAuth from "next-auth";
import Keycloak from "next-auth/providers/keycloak";

// Refresh this long before expiry so a token handed to the Api is never about to lapse mid-request.
const EXPIRY_MARGIN_SECONDS = 30;

type KeycloakTokens = { access_token: string; expires_in: number; refresh_token?: string };

export async function refreshAccessToken(refreshToken: string): Promise<KeycloakTokens> {
  const response = await fetch(`${process.env.AUTH_KEYCLOAK_ISSUER}/protocol/openid-connect/token`, {
    method: "POST",
    body: new URLSearchParams({
      client_id: process.env.AUTH_KEYCLOAK_ID!,
      client_secret: process.env.AUTH_KEYCLOAK_SECRET!,
      grant_type: "refresh_token",
      refresh_token: refreshToken,
    }),
  });
  const body = await response.json();
  if (!response.ok) throw new Error(`Refresh failed: ${JSON.stringify(body)}`);
  return body as KeycloakTokens;
}

/** Ends the app session and the Keycloak SSO session, then returns to the start screen. */
export async function federatedSignOut() {
  const session = await auth();
  await signOut({ redirect: false });
  const url = new URL(`${process.env.AUTH_KEYCLOAK_ISSUER}/protocol/openid-connect/logout`);
  url.searchParams.set("post_logout_redirect_uri", `${process.env.AUTH_URL ?? "https://localhost:3000"}/`);
  if (session?.idToken) url.searchParams.set("id_token_hint", session.idToken);
  else url.searchParams.set("client_id", process.env.AUTH_KEYCLOAK_ID ?? "web");
  redirect(url.toString());
}

export const { handlers, auth, signIn, signOut } = NextAuth({
  providers: [Keycloak],
  session: { strategy: "jwt" },
  pages: { signIn: "/" },
  callbacks: {
    authorized({ auth: session, request }) {
      // Route protection for proxy.ts: /app needs a session; signed-in users skip the start screen.
      const { pathname } = request.nextUrl;
      if (pathname.startsWith("/app")) return !!session?.user && !session.error;
      if (pathname === "/" && session?.user && !session.error)
        return Response.redirect(new URL("/app", request.nextUrl));
      return true;
    },
    async jwt({ token, account }) {
      if (account) {
        return {
          ...token,
          accessToken: account.access_token!,
          expiresAt: account.expires_at!,
          refreshToken: account.refresh_token,
          idToken: account.id_token,
        };
      }
      if (Date.now() / 1000 < token.expiresAt - EXPIRY_MARGIN_SECONDS) return token;
      if (!token.refreshToken) return { ...token, error: "RefreshTokenError" as const };

      try {
        const tokens = await refreshAccessToken(token.refreshToken);
        return {
          ...token,
          accessToken: tokens.access_token,
          expiresAt: Math.floor(Date.now() / 1000 + tokens.expires_in),
          refreshToken: tokens.refresh_token ?? token.refreshToken,
          error: undefined,
        };
      } catch (error) {
        console.error(error);
        return { ...token, error: "RefreshTokenError" as const };
      }
    },
    session({ session, token }) {
      // accessToken/idToken are stripped from the browser-facing /api/auth/session response (see the auth route).
      session.accessToken = token.accessToken;
      session.idToken = token.idToken;
      session.error = token.error;
      return session;
    },
  },
});

declare module "next-auth" {
  interface Session {
    accessToken?: string;
    idToken?: string;
    error?: "RefreshTokenError";
  }
}

declare module "@auth/core/jwt" {
  interface JWT {
    accessToken: string;
    expiresAt: number;
    refreshToken?: string;
    idToken?: string;
    error?: "RefreshTokenError";
  }
}
