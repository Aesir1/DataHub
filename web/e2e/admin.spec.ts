import { expect, test } from "@playwright/test";
import { login, users } from "./helpers";

test("platform admin lists queues and purges a dead-letter queue", async ({ page }) => {
  await login(page, users.admin);
  await page.getByRole("link", { name: "Admin · Queues" }).click();
  await expect(page.getByRole("cell", { name: "product-audit", exact: true })).toBeVisible();

  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("row", { name: /product-audit\.dlq/ })
    .getByRole("button", { name: "Purge" })
    .click();
  await expect(page.getByRole("status")).toHaveText(/Purged \d+ messages from product-audit\.dlq/);
});

test("a plain user cannot use the queue admin page", async ({ page }) => {
  await login(page, users.user);
  await page.goto("/app/admin/queues");
  await expect(page.getByRole("status")).toContainText(/not authorized/i);
});
