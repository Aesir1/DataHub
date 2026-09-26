import { expect, test } from "@playwright/test";
import { login, unique, users } from "./helpers";

test("products CRUD with validation and audit history", async ({ page }) => {
  const sku = unique("E2E-");
  const name = `Probe ${sku}`;
  await login(page, users.user);
  await page.getByRole("link", { name: "Products" }).click();
  const form = page.getByRole("form", { name: "New product" });

  // Validation error comes back as a typed error
  await form.getByLabel("SKU").fill("bad sku");
  await form.getByRole("button", { name: "Add product" }).click();
  await expect(form.getByText(/SKU must be/)).toBeVisible();

  // Create
  await form.getByLabel("Name").fill(name);
  await form.getByLabel("SKU").fill(sku);
  await form.getByLabel("Price").fill("12.50");
  await form.getByRole("button", { name: "Add product" }).click();
  const row = page.getByRole("row", { name: new RegExp(sku) });
  await expect(row).toContainText("12.50");

  // Update
  await row.getByRole("button", { name: "Edit" }).click();
  const edit = page.getByRole("form", { name: `Edit ${name}` });
  await edit.getByLabel("Price").fill("99");
  await edit.getByRole("button", { name: "Save" }).click();
  await expect(page.getByRole("row", { name: new RegExp(sku) })).toContainText("99.00");

  // Audit history arrives through outbox → RabbitMQ → consumer
  await page
    .getByRole("row", { name: new RegExp(sku) })
    .getByRole("button", { name: "History" })
    .click();
  const history = page.getByRole("region", { name: "Product history" });
  await expect(async () => {
    await history.getByRole("button", { name: "Refresh" }).click();
    await expect(history.getByText("updated")).toBeVisible({ timeout: 1_000 });
    await expect(history.getByText("created")).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 30_000 });

  // Delete
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("row", { name: new RegExp(sku) })
    .getByRole("button", { name: "Delete" })
    .click();
  await expect(page.getByRole("row", { name: new RegExp(sku) })).toHaveCount(0);
});
