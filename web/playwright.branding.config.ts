import path from "node:path";
import { defineConfig, devices } from "@playwright/test";

/**
 * TE-6: the same production build served twice with different BRANDING_FILE values; the start screen is
 * compared against committed screenshots. Needs no backend (the start screen is public).
 */
const serve = (port: number, brandingFile: string) => ({
  command: `node .next/standalone/server.js`,
  url: `http://localhost:${port}`,
  reuseExistingServer: false,
  timeout: 60_000,
  env: {
    PORT: String(port),
    HOSTNAME: "localhost",
    BRANDING_FILE: path.resolve(brandingFile),
    AUTH_SECRET: "branding-screenshot-test-secret",
    AUTH_TRUST_HOST: "true",
    AUTH_KEYCLOAK_ID: "web",
    AUTH_KEYCLOAK_SECRET: "unused",
    AUTH_KEYCLOAK_ISSUER: "http://localhost:8080/realms/datahub",
    OTEL_SDK_DISABLED: "true",
  },
});

export default defineConfig({
  testDir: "e2e",
  testMatch: /branding\.spec\.ts/,
  snapshotPathTemplate: "{testDir}/__screenshots__/{arg}{ext}",
  expect: { toHaveScreenshot: { maxDiffPixelRatio: 0.01 } },
  use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 720 } },
  webServer: [serve(3101, "config/branding.json"), serve(3102, "e2e/fixtures/branding-alt.json")],
});
