/** Public branding assets (logo, login background) served by the Api from the private `branding` bucket. */
export async function GET(_req: Request, { params }: { params: Promise<{ key: string[] }> }) {
  const key = (await params).key.map(encodeURIComponent).join("/");
  const res = await fetch(`${process.env.API_URL}/branding/${key}`, { cache: "no-store" });
  const headers = new Headers();
  for (const name of ["content-type", "content-length", "cache-control"]) {
    const value = res.headers.get(name);
    if (value) headers.set(name, value);
  }
  return new Response(res.body, { status: res.status, headers });
}
