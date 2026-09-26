"use client";

import { useCallback, useEffect, useState } from "react";
import { graphql } from "@/gql";
import type { ProductsQuery, ProductAuditQuery } from "@/gql/graphql";
import { formErrors, type FormErrors } from "@/lib/errors";
import { execute } from "@/lib/graphql";

const ProductsDocument = graphql(`
  query Products {
    products(first: 100, order: [{ name: ASC }]) {
      totalCount
      nodes {
        id
        name
        sku
        price
        isArchived
        rowVersion
        updatedAtUtc
        updatedBy
      }
    }
  }
`);

const CreateProductDocument = graphql(`
  mutation CreateProduct($input: CreateProductInput!) {
    createProduct(input: $input) {
      product {
        id
      }
      errors {
        __typename
        ... on Error {
          message
        }
        ... on ValidationError {
          fields {
            field
            messages
          }
        }
      }
    }
  }
`);

const UpdateProductDocument = graphql(`
  mutation UpdateProduct($input: UpdateProductInput!) {
    updateProduct(input: $input) {
      product {
        id
      }
      errors {
        __typename
        ... on Error {
          message
        }
        ... on ValidationError {
          fields {
            field
            messages
          }
        }
      }
    }
  }
`);

const DeleteProductDocument = graphql(`
  mutation DeleteProduct($input: DeleteProductInput!) {
    deleteProduct(input: $input) {
      errors {
        __typename
        ... on Error {
          message
        }
      }
    }
  }
`);

const ProductPermissionsDocument = graphql(`
  query ProductPermissions {
    viewer {
      permissions
    }
  }
`);

const ProductAuditDocument = graphql(`
  query ProductAudit($productId: UUID!) {
    productAudit(productId: $productId, first: 50) {
      nodes {
        id
        change
        name
        price
        occurredAtUtc
      }
    }
  }
`);

type Product = NonNullable<NonNullable<ProductsQuery["products"]>["nodes"]>[number];
type Audit = NonNullable<NonNullable<ProductAuditQuery["productAudit"]>["nodes"]>[number];

const money = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const when = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "medium" });
const input =
  "w-full rounded-md border border-border bg-background px-3 py-1.5 text-sm aria-[invalid=true]:border-error";
const button = "rounded-md border border-border px-3 py-1.5 text-sm hover:bg-surface disabled:opacity-50";
const primary =
  "rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-on-primary hover:opacity-90 disabled:opacity-50";

function FieldError({ errors, field }: { errors: FormErrors | null; field: string }) {
  const message = errors?.fields[field];
  return message ? (
    <p id={`${field}-error`} className="mt-1 text-xs text-error">
      {message}
    </p>
  ) : null;
}

function ErrorSummary({ errors }: { errors: FormErrors | null }) {
  return errors?.message ? (
    <p role="alert" className="text-sm text-error">
      {errors.message}
    </p>
  ) : null;
}

export function ProductsPage() {
  const [canWrite, setCanWrite] = useState(false);
  const [products, setProducts] = useState<Product[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [history, setHistory] = useState<string | null>(null);

  const reload = useCallback(
    () =>
      execute(ProductsDocument).then(
        (d) => {
          setProducts(d.products?.nodes ?? []);
          setLoadError(null);
        },
        (e: Error) => setLoadError(e.message),
      ),
    [],
  );

  useEffect(() => {
    void reload();
    execute(ProductPermissionsDocument).then(
      (d) => setCanWrite(d.viewer.permissions.includes("products.write")),
      () => setCanWrite(false),
    );
  }, [reload]);

  return (
    <main className="space-y-6 p-4">
      <h1 className="text-xl font-semibold">Products</h1>
      {canWrite && <CreateProductForm onCreated={reload} />}
      {loadError && <p className="text-sm text-error">{loadError}</p>}
      {products?.length === 0 && <p className="text-muted">No products yet.</p>}
      {!!products?.length && (
        <table className="w-full max-w-4xl text-sm">
          <thead>
            <tr className="border-b border-border text-left text-muted">
              <th scope="col" className="py-2 font-medium">
                Name
              </th>
              <th scope="col" className="py-2 font-medium">
                SKU
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Price
              </th>
              <th scope="col" className="py-2 font-medium">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {products.map((p) =>
              editing === p.id ? (
                <EditProductRow
                  key={p.id}
                  product={p}
                  onDone={() => (setEditing(null), reload())}
                  onCancel={() => setEditing(null)}
                />
              ) : (
                <tr key={p.id} className="border-b border-border">
                  <td className="py-2">{p.name}</td>
                  <td className="py-2 font-mono">{p.sku}</td>
                  <td className="py-2 text-right tabular-nums">{money.format(p.price)}</td>
                  <td className="space-x-2 py-2 text-right">
                    <button
                      type="button"
                      className={button}
                      onClick={() => setHistory(history === p.id ? null : p.id)}
                    >
                      History
                    </button>
                    {canWrite && (
                      <>
                        <button type="button" className={button} onClick={() => setEditing(p.id)}>
                          Edit
                        </button>
                        <DeleteProductButton product={p} onDeleted={reload} />
                      </>
                    )}
                  </td>
                </tr>
              ),
            )}
          </tbody>
        </table>
      )}
      {history && (
        <ProductHistory
          key={history}
          productId={history}
          name={products?.find((p) => p.id === history)?.name}
        />
      )}
    </main>
  );
}

function CreateProductForm({ onCreated }: { onCreated: () => void }) {
  const [errors, setErrors] = useState<FormErrors | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(form: FormData) {
    setBusy(true);
    try {
      const { createProduct } = await execute(CreateProductDocument, {
        input: {
          name: String(form.get("name")),
          sku: String(form.get("sku")),
          price: Number(form.get("price")),
        },
      });
      const problems = formErrors(createProduct.errors);
      setErrors(problems);
      if (!problems) {
        (document.getElementById("create-product") as HTMLFormElement | null)?.reset();
        onCreated();
      }
    } catch (e) {
      setErrors({ message: (e as Error).message, fields: {} });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      id="create-product"
      action={submit}
      className="grid max-w-4xl gap-3 rounded-lg border border-border p-4 md:grid-cols-[2fr_1fr_1fr_auto] md:items-start"
      aria-label="New product"
    >
      <div>
        <label htmlFor="new-name" className="text-xs text-muted">
          Name
        </label>
        <input
          id="new-name"
          name="name"
          className={input}
          aria-invalid={!!errors?.fields.name}
          aria-describedby="name-error"
        />
        <FieldError errors={errors} field="name" />
      </div>
      <div>
        <label htmlFor="new-sku" className="text-xs text-muted">
          SKU
        </label>
        <input
          id="new-sku"
          name="sku"
          className={`${input} font-mono`}
          aria-invalid={!!errors?.fields.sku}
          aria-describedby="sku-error"
        />
        <FieldError errors={errors} field="sku" />
      </div>
      <div>
        <label htmlFor="new-price" className="text-xs text-muted">
          Price
        </label>
        <input
          id="new-price"
          name="price"
          type="number"
          step="0.01"
          defaultValue="0"
          className={input}
          aria-invalid={!!errors?.fields.price}
          aria-describedby="price-error"
        />
        <FieldError errors={errors} field="price" />
      </div>
      <button type="submit" disabled={busy} className={`${primary} md:mt-5`}>
        Add product
      </button>
      <div className="md:col-span-4">
        <ErrorSummary errors={errors} />
      </div>
    </form>
  );
}

function EditProductRow({
  product,
  onDone,
  onCancel,
}: {
  product: Product;
  onDone: () => void;
  onCancel: () => void;
}) {
  const [errors, setErrors] = useState<FormErrors | null>(null);

  async function submit(form: FormData) {
    try {
      const { updateProduct } = await execute(UpdateProductDocument, {
        input: {
          id: product.id,
          name: String(form.get("name")),
          price: Number(form.get("price")),
          isArchived: form.get("isArchived") === "on",
          rowVersion: product.rowVersion,
        },
      });
      const problems = formErrors(updateProduct.errors);
      if (updateProduct.errors?.some((e) => e.__typename === "ConcurrencyError")) {
        setErrors({
          message: "Someone else changed this product. Cancel to reload the latest version.",
          fields: {},
        });
      } else {
        setErrors(problems);
      }
      if (!problems) onDone();
    } catch (e) {
      setErrors({ message: (e as Error).message, fields: {} });
    }
  }

  return (
    <tr className="border-b border-border bg-surface">
      <td colSpan={4} className="p-2">
        <form
          action={submit}
          aria-label={`Edit ${product.name}`}
          className="grid gap-3 md:grid-cols-[2fr_1fr_auto_auto] md:items-start"
        >
          <div>
            <label htmlFor={`name-${product.id}`} className="text-xs text-muted">
              Name
            </label>
            <input
              id={`name-${product.id}`}
              name="name"
              defaultValue={product.name}
              className={input}
              aria-invalid={!!errors?.fields.name}
            />
            <FieldError errors={errors} field="name" />
          </div>
          <div>
            <label htmlFor={`price-${product.id}`} className="text-xs text-muted">
              Price
            </label>
            <input
              id={`price-${product.id}`}
              name="price"
              type="number"
              step="0.01"
              defaultValue={product.price}
              className={input}
              aria-invalid={!!errors?.fields.price}
            />
            <FieldError errors={errors} field="price" />
          </div>
          <label className="flex items-center gap-2 text-sm md:mt-6">
            <input type="checkbox" name="isArchived" defaultChecked={product.isArchived} /> Archived
          </label>
          <div className="space-x-2 md:mt-5">
            <button type="submit" className={primary}>
              Save
            </button>
            <button type="button" className={button} onClick={onCancel}>
              Cancel
            </button>
          </div>
          <div className="md:col-span-4">
            <ErrorSummary errors={errors} />
          </div>
        </form>
      </td>
    </tr>
  );
}

function DeleteProductButton({ product, onDeleted }: { product: Product; onDeleted: () => void }) {
  const [error, setError] = useState<string | null>(null);
  async function remove() {
    if (!confirm(`Delete ${product.name}?`)) return;
    const { deleteProduct } = await execute(DeleteProductDocument, { input: { id: product.id } });
    const problems = formErrors(deleteProduct.errors);
    setError(problems?.message ?? null);
    if (!problems) onDeleted();
  }
  return (
    <>
      <button type="button" className={button} onClick={remove}>
        Delete
      </button>
      {error && <span className="text-xs text-error">{error}</span>}
    </>
  );
}

/** Audit rows arrive asynchronously (outbox → RabbitMQ → consumer), hence the refresh button. */
function ProductHistory({ productId, name }: { productId: string; name?: string }) {
  const [rows, setRows] = useState<Audit[] | null>(null);
  const load = useCallback(
    () => execute(ProductAuditDocument, { productId }).then((d) => setRows(d.productAudit?.nodes ?? [])),
    [productId],
  );
  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section aria-label="Product history" className="max-w-4xl rounded-lg border border-border p-4">
      <div className="mb-2 flex items-center justify-between">
        <h2 className="font-semibold">History · {name}</h2>
        <button type="button" className={button} onClick={() => void load()}>
          Refresh
        </button>
      </div>
      {rows?.length === 0 && <p className="text-sm text-muted">No events recorded yet.</p>}
      <ol className="space-y-1 text-sm">
        {rows?.map((a) => (
          <li key={a.id} className="flex gap-3">
            <time dateTime={a.occurredAtUtc} className="text-muted">
              {when.format(new Date(a.occurredAtUtc))}
            </time>
            <span className="font-medium">{a.change.toLowerCase()}</span>
            <span>
              {a.name} · {money.format(a.price)}
            </span>
          </li>
        ))}
      </ol>
    </section>
  );
}
