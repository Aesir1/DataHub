import { expect, test } from "@playwright/test";

/** TE-6: one build, two branding.json files, two different start screens. */
test("start screen follows the branding file", async ({ page }) => {
  await page.goto("http://localhost:3101/");
  await expect(page.getByRole("heading", { name: "DataHub" })).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  await expect(page).toHaveScreenshot("start-datahub.png");
  const primary = await page
    .getByRole("button", { name: "Sign in or register" })
    .evaluate((b) => getComputedStyle(b).backgroundColor);

  await page.goto("http://localhost:3102/");
  await expect(page.getByRole("heading", { name: "Acme Cold Chain" })).toBeVisible();
  await expect(page).toHaveTitle("Acme Cold Chain");
  await expect(page).toHaveScreenshot("start-acme.png");
  const altPrimary = await page
    .getByRole("button", { name: "Sign in or register" })
    .evaluate((b) => getComputedStyle(b).backgroundColor);

  expect(altPrimary).not.toBe(primary);
});
