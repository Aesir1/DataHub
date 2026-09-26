import type { CodegenConfig } from "@graphql-codegen/cli";

const config: CodegenConfig = {
  schema: "schema.graphql",
  documents: ["src/**/*.{ts,tsx}", "!src/gql/**/*"],
  ignoreNoDocuments: true,
  generates: {
    "src/gql/": {
      preset: "client",
      config: {
        documentMode: "string",
        useTypeImports: true,
        enumsAsTypes: true,
        scalars: {
          UUID: "string",
          DateTime: "string",
          Decimal: "number",
          UnsignedInt: "number",
          Long: "number",
          URL: "string",
          URI: "string",
        },
      },
    },
  },
};

export default config;
