import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  test: {
    environment: "happy-dom",
    environmentOptions: { happyDOM: { url: "http://localhost:3000" } },
    setupFiles: ["./vitest.setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      include: ["src/**/*.{ts,tsx}"],
      exclude: ["src/gql/**", "src/**/*.test.{ts,tsx}", "src/test/**"],
      reporter: ["text-summary", "lcov", "cobertura"],
    },
  },
});
