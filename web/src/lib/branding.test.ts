import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  brandingCss,
  contrast,
  contrastWarnings,
  googleFontsUrl,
  parseBranding,
  resolveAsset,
} from "./branding";

const committed = () => JSON.parse(readFileSync("config/branding.json", "utf8"));
const alt = () => JSON.parse(readFileSync("e2e/fixtures/branding-alt.json", "utf8"));

describe("branding", () => {
  it("accepts the committed branding files", () => {
    expect(parseBranding(committed()).name).toBe("DataHub");
    expect(parseBranding(alt()).authScreen.cardPosition).toBe("left");
  });

  it("names the offending path when invalid", () => {
    const bad = committed();
    bad.palette.light.primary = "blue";
    delete bad.authScreen.overlay;
    expect(() => parseBranding(bad, "x.json")).toThrowError(
      /Invalid branding file x\.json: palette\.light\.primary: must be a #RRGGBB colour; authScreen\.overlay:/,
    );
  });

  it("computes WCAG contrast", () => {
    expect(contrast("#000000", "#FFFFFF")).toBeCloseTo(21, 5);
    expect(contrast("#FFFFFF", "#FFFFFF")).toBe(1);
    expect(contrast("#0F62FE", "#FFFFFF")).toBeGreaterThan(4.5);
  });

  it("warns for pairs below 4.5:1 in either scheme", () => {
    expect(contrastWarnings(parseBranding(committed()))).toEqual([]);
    const weak = committed();
    weak.palette.light.onPrimary = "#88AAFF";
    weak.palette.dark.foreground = "#222222";
    const warnings = contrastWarnings(parseBranding(weak));
    expect(warnings).toHaveLength(2);
    expect(warnings[0]).toMatch(/^branding light: onPrimary\/primary contrast/);
    expect(warnings[1]).toMatch(/^branding dark: foreground\/background contrast/);
  });

  it("emits light, dark and prefers-color-scheme custom properties", () => {
    const css = brandingCss(parseBranding(committed()));
    expect(css).toContain(":root{--brand-primary:#0F62FE;");
    expect(css).toContain("[data-theme=dark]{--brand-primary:#78A9FF;");
    expect(css).toContain("@media (prefers-color-scheme: dark){:root:not([data-theme=light]){");
    expect(css).toContain('--brand-font-heading:"Poppins", system-ui');
    // dark falls back to light for keys it omits
    expect(css).toMatch(/\[data-theme=dark\]\{[^}]*--brand-accent:#FF832B;/);
  });

  it("builds a Google Fonts URL only for google fonts and @font-face for self-hosted ones", () => {
    expect(googleFontsUrl(parseBranding(committed()))).toBe(
      "https://fonts.googleapis.com/css2?family=Poppins:wght@600;700&family=Inter:wght@400;500&display=swap",
    );
    expect(googleFontsUrl(parseBranding(alt()))).toBeNull();
    const hosted = committed();
    hosted.fonts.body = { family: "Brand Sans", source: "url", url: "/fonts/brand.woff2" };
    expect(brandingCss(parseBranding(hosted))).toContain(
      '@font-face{font-family:"Brand Sans";src:url("/fonts/brand.woff2") format("woff2")',
    );
  });

  it("resolves s3:// assets against the public MinIO URL", () => {
    expect(resolveAsset("s3://branding/login-bg.jpg", "http://minio.test/")).toBe(
      "http://minio.test/branding/login-bg.jpg",
    );
    expect(resolveAsset("/brand/logo.svg", "http://minio.test")).toBe("/brand/logo.svg");
  });
});
