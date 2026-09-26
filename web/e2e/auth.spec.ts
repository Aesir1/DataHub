import { expect, test } from "@playwright/test";
import { login, unique, users } from "./helpers";

test("start screen is branded", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveTitle("DataHub");
  await expect(page.getByRole("heading", { name: "DataHub" })).toBeVisible();
  await expect(page.getByText("Container temperatures from every webhook, in one place.")).toBeVisible();
  await expect(page.getByTestId("start-screen")).toHaveCSS("background-image", /login-bg\.svg/);
});

test("unauthenticated /app redirects to the start screen", async ({ page }) => {
  await page.goto("/app/products");
  await expect(page).toHaveURL(/\/($|\?)/);
  await expect(page.getByRole("button", { name: "Sign in or register" })).toBeVisible();
});

test("a new user registers through Keycloak and reaches /app", async ({ page }) => {
  const email = `${unique("e2e-").toLowerCase()}@local.test`;
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in or register" }).click();
  await page.getByRole("link", { name: "Register" }).click();
  await page.getByRole("textbox", { name: "Email", exact: true }).fill(email);
  await page.getByRole("textbox", { name: "Password", exact: true }).fill("E2e-Pass-123!");
  await page.getByRole("textbox", { name: "Confirm password", exact: true }).fill("E2e-Pass-123!");
  await page.getByRole("textbox", { name: "First name", exact: true }).fill("E2E");
  await page.getByRole("textbox", { name: "Last name", exact: true }).fill("Tester");
  await page.getByRole("button", { name: "Register" }).click();

  await expect(page).toHaveURL(/\/app$/);
  await expect(page.getByText(email)).toBeVisible();
  await expect(page.getByRole("link", { name: "Products" })).toBeVisible();
});

test("user signs in, sees no admin link, and signs out of Keycloak too", async ({ page }) => {
  await login(page, users.user);
  await expect(page.getByRole("link", { name: "Containers" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Admin · Queues" })).toHaveCount(0);

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("button", { name: "Sign in or register" })).toBeVisible();
  await page.getByRole("button", { name: "Sign in or register" }).click();
  await expect(page.getByLabel("Email")).toBeVisible(); // SSO session ended: Keycloak asks again
});
