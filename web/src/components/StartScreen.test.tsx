import { readFileSync } from "node:fs";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { parseBranding } from "@/lib/branding";
import { StartScreen } from "./StartScreen";

const load = (file: string) => parseBranding(JSON.parse(readFileSync(file, "utf8")));

describe("StartScreen", () => {
  it("renders name, tagline, logo and a right-hand card over the background image", () => {
    const branding = load("config/branding.json");
    render(<StartScreen branding={branding} action={async () => {}} />);

    expect(screen.getByRole("heading", { name: "DataHub" })).toBeTruthy();
    expect(screen.getByText(branding.authScreen.tagline!)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign in or register" })).toBeTruthy();
    const main = screen.getByTestId("start-screen");
    expect(main.style.backgroundImage).toContain("/brand/login-bg.svg");
    expect(main.querySelector(".justify-end")).toBeTruthy();
    expect(main.querySelector("img")?.getAttribute("src")).toBe("/brand/logo.svg");
  });

  it("supports a colour background, left card and s3 images", () => {
    const branding = load("e2e/fixtures/branding-alt.json");
    branding.logo = "s3://branding/logo.png";
    render(<StartScreen branding={branding} action={async () => {}} />);

    const main = screen.getByTestId("start-screen");
    expect(main.style.backgroundColor).toBeTruthy();
    expect(main.querySelector(".justify-start")).toBeTruthy();
    expect(main.querySelector("img")?.getAttribute("src")).toBe("/api/branding/logo.png");
  });
});
