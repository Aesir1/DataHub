import { describe, expect, it } from "vitest";
import { formErrors } from "./errors";

describe("formErrors", () => {
  it("returns null without errors", () => {
    expect(formErrors(null)).toBeNull();
    expect(formErrors([])).toBeNull();
  });

  it("maps validation fields to lower-camel keys and keeps other messages", () => {
    expect(
      formErrors([
        {
          __typename: "ValidationError",
          message: "Validation failed.",
          fields: [{ field: "Sku", messages: ["bad", "short"] }],
        },
      ]),
    ).toEqual({ message: "Please fix the highlighted fields.", fields: { sku: "bad short" } });
    expect(formErrors([{ __typename: "ConflictError", message: "duplicate" }])).toEqual({
      message: "duplicate",
      fields: {},
    });
  });
});
