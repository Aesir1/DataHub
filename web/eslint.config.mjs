import next from "eslint-config-next";

const config = [{ ignores: ["src/gql/**", ".next/**", "coverage/**", "test-results/**", "playwright-report/**"] }, ...next];
export default config;
