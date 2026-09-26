import { defineConfig, devices } from "@playwright/test";

/**
 * TE-5: end-to-end tests against the stack started by Aspire (`dotnet run --project aspire/DataHub.AppHost`,
 * or Aspire.Hosting.Testing in CI) with the seeded Keycloak users.
 */
export default defineConfig({
  testDir: "e2e",
  testIgnore: /branding\.spec\.ts/,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["junit", { outputFile: "test-results/e2e-junit.xml" }]] : "list",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
    ...devices["Desktop Chrome"],
  },
});
