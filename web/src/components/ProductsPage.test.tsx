import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { graphql, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { server } from "@/test/server";
import { ProductsPage } from "./ProductsPage";

const products = [
  {
    id: "p1",
    name: "Reefer",
    sku: "RF-1",
    price: 1850,
    isArchived: false,
    rowVersion: 7,
    updatedAtUtc: null,
    updatedBy: null,
  },
];

function mockBase(permissions = ["products.read", "products.write"]) {
  server.use(
    graphql.query("Products", () =>
      HttpResponse.json({ data: { products: { totalCount: 1, nodes: products } } }),
    ),
    graphql.query("ProductPermissions", () => HttpResponse.json({ data: { viewer: { permissions } } })),
  );
}

describe("ProductsPage", () => {
  it("lists products from the Api", async () => {
    mockBase();
    render(<ProductsPage />);

    const row = (await screen.findByText("Reefer")).closest("tr")!;
    expect(within(row).getByText("RF-1")).toBeTruthy();
    expect(within(row).getByText(/1,850\.00|1850\.00/)).toBeTruthy();
  });

  it("hides write actions without products.write", async () => {
    mockBase(["products.read"]);
    render(<ProductsPage />);

    await screen.findByText("Reefer");
    expect(screen.queryByRole("form", { name: "New product" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });

  it("shows ValidationError fields next to the inputs", async () => {
    mockBase();
    server.use(
      graphql.mutation("CreateProduct", () =>
        HttpResponse.json({
          data: {
            createProduct: {
              product: null,
              errors: [
                {
                  __typename: "ValidationError",
                  message: "Validation failed.",
                  fields: [{ field: "Sku", messages: ["SKU must be 3-32 characters."] }],
                },
              ],
            },
          },
        }),
      ),
    );
    render(<ProductsPage />);

    const form = await screen.findByRole("form", { name: "New product" });
    fireEvent.change(within(form).getByLabelText("SKU"), { target: { value: "x" } });
    fireEvent.submit(form);

    expect(await screen.findByText("SKU must be 3-32 characters.")).toBeTruthy();
    expect(within(form).getByLabelText("SKU").getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByRole("alert").textContent).toBe("Please fix the highlighted fields.");
  });

  it("shows ConflictError and ConcurrencyError messages", async () => {
    mockBase();
    server.use(
      graphql.mutation("CreateProduct", () =>
        HttpResponse.json({
          data: {
            createProduct: {
              product: null,
              errors: [{ __typename: "ConflictError", message: "SKU already exists." }],
            },
          },
        }),
      ),
      graphql.mutation("UpdateProduct", ({ variables }) => {
        expect(variables.input.rowVersion).toBe(7);
        return HttpResponse.json({
          data: {
            updateProduct: {
              product: null,
              errors: [{ __typename: "ConcurrencyError", message: "changed" }],
            },
          },
        });
      }),
    );
    render(<ProductsPage />);

    fireEvent.submit(await screen.findByRole("form", { name: "New product" }));
    expect(await screen.findByText("SKU already exists.")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.submit(screen.getByRole("form", { name: "Edit Reefer" }));
    expect(await screen.findByText(/Someone else changed this product/)).toBeTruthy();
  });

  it("shows audit history", async () => {
    mockBase();
    server.use(
      graphql.query("ProductAudit", () =>
        HttpResponse.json({
          data: {
            productAudit: {
              nodes: [
                {
                  id: "a1",
                  change: "CREATED",
                  name: "Reefer",
                  price: 1850,
                  occurredAtUtc: "2026-09-25T10:00:00Z",
                },
              ],
            },
          },
        }),
      ),
    );
    render(<ProductsPage />);

    fireEvent.click(await screen.findByRole("button", { name: "History" }));
    const history = await screen.findByRole("region", { name: "Product history" });
    await waitFor(() => expect(within(history).getByText("created")).toBeTruthy());
  });
});
