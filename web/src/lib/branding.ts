import { z } from "zod";

const hex = z.string().regex(/^#[0-9a-fA-F]{6}$/, "must be a #RRGGBB colour");

const font = z.object({
  family: z.string().min(1),
  /** google: Google Fonts family; url: self-hosted woff2; system: installed font only. */
  source: z.enum(["google", "url", "system"]),
  url: z.string().optional(),
  weights: z.array(z.number().int().min(100).max(900)).optional(),
});

const lightPalette = z.object({
  primary: hex,
  onPrimary: hex,
  secondary: hex,
  accent: hex,
  background: hex,
  foreground: hex,
  success: hex,
  warning: hex,
  error: hex,
});

export const brandingSchema = z.object({
  name: z.string().min(1),
  logo: z.string().min(1),
  favicon: z.string().min(1).optional(),
  fonts: z.object({ heading: font, body: font }),
  palette: z.object({ light: lightPalette, dark: lightPalette.partial().optional() }),
  authScreen: z.object({
    background: z.object({ type: z.enum(["image", "color"]), src: z.string().min(1) }),
    overlay: z.object({ color: hex, opacity: z.number().min(0).max(1) }),
    cardPosition: z.enum(["left", "center", "right"]),
    tagline: z.string().optional(),
  }),
});

export type Branding = z.infer<typeof brandingSchema>;
export type Palette = z.infer<typeof lightPalette>;

/** Parses branding JSON; the error names the offending path, e.g. `palette.light.primary: must be a #RRGGBB colour`. */
export function parseBranding(json: unknown, file = "branding.json"): Branding {
  const result = brandingSchema.safeParse(json);
  if (!result.success) {
    const issues = result.error.issues.map((i) => `${i.path.join(".") || "(root)"}: ${i.message}`).join("; ");
    throw new Error(`Invalid branding file ${file}: ${issues}`);
  }
  return result.data;
}

/** `s3://bucket/key` → public object URL (MinIO `branding` bucket is anonymously readable); anything else unchanged. */
export function resolveAsset(
  src: string,
  minioPublicUrl = process.env.MINIO_PUBLIC_URL ?? "http://localhost:9000",
): string {
  const match = /^s3:\/\/([^/]+)\/(.+)$/.exec(src);
  return match ? `${minioPublicUrl.replace(/\/$/, "")}/${match[1]}/${match[2]}` : src;
}

const cssVar: Record<keyof Palette, string> = {
  primary: "--brand-primary",
  onPrimary: "--brand-on-primary",
  secondary: "--brand-secondary",
  accent: "--brand-accent",
  background: "--brand-background",
  foreground: "--brand-foreground",
  success: "--brand-success",
  warning: "--brand-warning",
  error: "--brand-error",
};

const declarations = (p: Partial<Palette>) =>
  (Object.keys(cssVar) as (keyof Palette)[])
    .filter((k) => p[k])
    .map((k) => `${cssVar[k]}:${p[k]};`)
    .join("");

const fallback = (family: string) =>
  `"${family}", system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`;

/** FE-11/FE-12: palette as CSS custom properties (light on :root, dark on [data-theme=dark] and prefers-color-scheme) plus fonts. */
export function brandingCss(b: Branding): string {
  const dark = { ...b.palette.light, ...b.palette.dark };
  const faces = [b.fonts.heading, b.fonts.body]
    .filter((f) => f.source === "url" && f.url)
    .map(
      (f) => `@font-face{font-family:"${f.family}";src:url("${f.url}") format("woff2");font-display:swap;}`,
    )
    .join("");
  return (
    faces +
    `:root{${declarations(b.palette.light)}--brand-font-heading:${fallback(b.fonts.heading.family)};--brand-font-body:${fallback(b.fonts.body.family)};}` +
    `[data-theme=dark]{${declarations(dark)}}` +
    `@media (prefers-color-scheme: dark){:root:not([data-theme=light]){${declarations(dark)}}}`
  );
}

/** Google Fonts stylesheet URL for fonts with source "google", or null. */
export function googleFontsUrl(b: Branding): string | null {
  const families = [b.fonts.heading, b.fonts.body]
    .filter((f, i, all) => f.source === "google" && all.findIndex((o) => o.family === f.family) === i)
    .map((f) => {
      const weights = [
        ...new Set(
          [b.fonts.heading, b.fonts.body]
            .filter((o) => o.family === f.family)
            .flatMap((o) => o.weights ?? [400]),
        ),
      ].sort();
      return `family=${encodeURIComponent(f.family).replace(/%20/g, "+")}:wght@${weights.join(";")}`;
    });
  return families.length ? `https://fonts.googleapis.com/css2?${families.join("&")}&display=swap` : null;
}

function luminance(color: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(color.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** WCAG 2.x contrast ratio, 1–21. */
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/** FE-14: pairs below WCAG AA 4.5:1, per scheme. */
export function contrastWarnings(b: Branding): string[] {
  const schemes: [string, Palette][] = [["light", b.palette.light]];
  if (b.palette.dark) schemes.push(["dark", { ...b.palette.light, ...b.palette.dark }]);
  return schemes.flatMap(([scheme, p]) =>
    (
      [
        ["foreground", "background"],
        ["onPrimary", "primary"],
      ] as const
    )
      .map(([fg, bg]) => ({ fg, bg, ratio: contrast(p[fg], p[bg]) }))
      .filter((x) => x.ratio < 4.5)
      .map(
        (x) => `branding ${scheme}: ${x.fg}/${x.bg} contrast ${x.ratio.toFixed(2)}:1 is below WCAG AA 4.5:1`,
      ),
  );
}
