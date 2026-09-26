import { expect, type Page } from "@playwright/test";

export const users = {
  user: { email: "user@local.test", password: process.env.E2E_USER_PASSWORD ?? "User123!" },
  admin: { email: "admin@local.test", password: process.env.E2E_ADMIN_PASSWORD ?? "Admin123!" },
};

/** Start screen → Keycloak → back in /app. */
export async function login(page: Page, { email, password }: { email: string; password: string }) {
  await page.goto("/");
  await page.getByRole("button", { name: "Sign in or register" }).click();
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign In" }).click();
  await expect(page).toHaveURL(/\/app$/);
}

export const unique = (prefix: string) =>
  `${prefix}${Date.now().toString(36).toUpperCase()}${Math.floor(Math.random() * 1000)}`;
