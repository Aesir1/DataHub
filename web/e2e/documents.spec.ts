import { expect, test } from "@playwright/test";
import { login, unique, users } from "./helpers";

test("documents upload, download, rename, replace and delete through the Api", async ({
  page,
  request,
}) => {
  const fileName = `${unique("e2e-")}.txt`;
  await login(page, users.user);
  await page.getByRole("link", { name: "Documents" }).click();

  await page
    .locator('main input[type="file"]')
    .first()
    .setInputFiles({ name: fileName, mimeType: "text/plain", buffer: Buffer.from("hello e2e") });
  const row = page.getByRole("row", { name: new RegExp(fileName) });
  await expect(row).toContainText("Available");

  // Same-origin route with the session cookie; without it (no browser session) the route refuses.
  const href = await row.getByRole("link", { name: fileName }).getAttribute("href");
  expect(href).toMatch(/^\/api\/documents\/[0-9a-f-]+\/content$/);
  expect(await (await page.request.get(href!)).text()).toBe("hello e2e");
  expect((await request.get(href!)).status()).toBe(401);

  await row.getByRole("button", { name: "Rename" }).click();
  const renamed = `renamed-${fileName}`;
  await page.getByRole("textbox", { name: "New name" }).fill(renamed);
  await page.getByRole("button", { name: "Save" }).click();
  const renamedRow = page.getByRole("row", { name: new RegExp(renamed) });
  await expect(renamedRow).toBeVisible();

  await renamedRow
    .getByLabel(`Replace ${renamed}`)
    .setInputFiles({ name: renamed, mimeType: "text/plain", buffer: Buffer.from("second version") });
  // Same object key, new bytes: poll the download link until the replacement is served.
  await expect
    .poll(async () => {
      const href = await renamedRow.getByRole("link", { name: renamed }).getAttribute("href");
      return href ? (await page.request.get(href)).text() : null;
    })
    .toBe("second version");

  page.once("dialog", (d) => d.accept());
  await renamedRow.getByRole("button", { name: "Delete" }).click();
  await expect(page.getByRole("row", { name: new RegExp(renamed) })).toHaveCount(0);
});
