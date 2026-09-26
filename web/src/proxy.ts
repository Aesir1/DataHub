export { auth as proxy } from "@/auth";

export const config = {
  // /api is excluded: the auth and graphql routes do their own session handling.
  matcher: ["/", "/app/:path*"],
};
