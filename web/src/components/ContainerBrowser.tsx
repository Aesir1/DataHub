"use client";

import { useCallback, useEffect, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { graphql } from "@/gql";
import type { ContainersQuery, TemperaturesQuery } from "@/gql/graphql";
import { execute } from "@/lib/graphql";

const ContainersDocument = graphql(`
  query Containers($after: String) {
    containers(first: 100, after: $after) {
      totalCount
      pageInfo {
        hasNextPage
        endCursor
      }
      nodes {
        id
        code
      }
    }
  }
`);

const TemperaturesDocument = graphql(`
  query Temperatures($containerId: UUID!, $after: String) {
    temperatures(containerId: $containerId, first: 100, after: $after) {
      pageInfo {
        hasNextPage
        endCursor
      }
      nodes {
        id
        timestampUtc
        celsius
      }
    }
  }
`);

type Container = NonNullable<NonNullable<ContainersQuery["containers"]>["nodes"]>[number];
type Reading = NonNullable<NonNullable<TemperaturesQuery["temperatures"]>["nodes"]>[number];

type Page<T> = { nodes: T[]; endCursor?: string | null; hasNextPage: boolean };

/** Loads the first cursor page on mount, then appends pages on demand. Remount (key) to start over. */
function usePaged<T>(load: (after: string | null) => Promise<Page<T>>) {
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchPage = useCallback(
    (after: string | null) =>
      load(after).then(
        (page) => {
          setItems((prev) => (after === null ? page.nodes : [...prev, ...page.nodes]));
          setCursor(page.endCursor ?? null);
          setHasMore(page.hasNextPage);
          setError(null);
          setLoading(false);
        },
        (e: unknown) => {
          setError(e instanceof Error ? e.message : String(e));
          setLoading(false);
        },
      ),
    [load],
  );

  useEffect(() => {
    void fetchPage(null);
  }, [fetchPage]);

  const loadMore = () => {
    setLoading(true);
    void fetchPage(cursor);
  };

  return { items, hasMore, loading, error, loadMore };
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "medium" });

export function ContainerBrowser() {
  const router = useRouter();
  const pathname = usePathname();
  const selectedId = useSearchParams().get("c");

  const loadContainers = useCallback(async (after: string | null) => {
    const data = await execute(ContainersDocument, { after });
    return {
      nodes: data.containers?.nodes ?? [],
      endCursor: data.containers?.pageInfo.endCursor,
      hasNextPage: data.containers?.pageInfo.hasNextPage ?? false,
    };
  }, []);
  const containers = usePaged<Container>(loadContainers);
  const selected = containers.items.find((c) => c.id === selectedId);

  return (
    <main className="grid flex-1 grid-cols-1 md:grid-cols-[18rem_1fr]">
      <nav aria-label="Containers" className="border-b border-border p-4 md:border-r md:border-b-0">
        <h2 className="mb-3 text-sm font-semibold tracking-wide text-muted uppercase">Containers</h2>
        {containers.error && <p className="text-sm text-error">{containers.error}</p>}
        {!containers.loading && !containers.error && containers.items.length === 0 && (
          <p className="text-sm text-muted">No containers yet. Send a webhook to /api/webhooks/container.</p>
        )}
        <ul className="space-y-1">
          {containers.items.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                aria-current={c.id === selectedId ? "true" : undefined}
                onClick={() => router.replace(`${pathname}?c=${c.id}`)}
                className="w-full rounded-md px-3 py-2 text-left font-mono text-sm hover:bg-surface aria-[current]:bg-primary aria-[current]:text-on-primary"
              >
                {c.code}
              </button>
            </li>
          ))}
        </ul>
        {containers.hasMore && (
          <button type="button" onClick={containers.loadMore} className="mt-2 text-sm text-primary underline">
            Load more containers
          </button>
        )}
      </nav>

      <section aria-live="polite" className="p-4">
        {selectedId ? (
          <Readings key={selectedId} containerId={selectedId} code={selected?.code} />
        ) : (
          <p className="text-muted">Select a container to see its temperatures.</p>
        )}
      </section>
    </main>
  );
}

function Readings({ containerId, code }: { containerId: string; code?: string }) {
  const loadReadings = useCallback(
    async (after: string | null) => {
      const data = await execute(TemperaturesDocument, { containerId, after });
      return {
        nodes: data.temperatures?.nodes ?? [],
        endCursor: data.temperatures?.pageInfo.endCursor,
        hasNextPage: data.temperatures?.pageInfo.hasNextPage ?? false,
      };
    },
    [containerId],
  );
  const readings = usePaged<Reading>(loadReadings);

  return (
    <>
      <h2 className="mb-3 text-lg font-semibold">
        <span className="font-mono">{code ?? "Container"}</span> temperatures
      </h2>
      {readings.error && <p className="text-sm text-error">{readings.error}</p>}
      {!readings.loading && !readings.error && readings.items.length === 0 && (
        <p className="text-muted">No readings for this container.</p>
      )}
      {readings.items.length > 0 && <ReadingsTable readings={readings.items} />}
      {readings.loading && <p className="mt-2 text-sm text-muted">Loading…</p>}
      {readings.hasMore && !readings.loading && (
        <button type="button" onClick={readings.loadMore} className="mt-3 text-sm text-primary underline">
          Load more readings
        </button>
      )}
    </>
  );
}

export function ReadingsTable({ readings }: { readings: Reading[] }) {
  return (
    <table className="w-full max-w-xl text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted">
          <th scope="col" className="py-2 font-medium">
            Timestamp
          </th>
          <th scope="col" className="py-2 text-right font-medium">
            Temperature (°C)
          </th>
        </tr>
      </thead>
      <tbody>
        {readings.map((r) => (
          <tr key={r.id} className="border-b border-border">
            <td className="py-2">
              <time dateTime={r.timestampUtc}>{dateFormat.format(new Date(r.timestampUtc))}</time>
            </td>
            <td className="py-2 text-right tabular-nums">{r.celsius.toFixed(1)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
