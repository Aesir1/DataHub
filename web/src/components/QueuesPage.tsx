"use client";

import { useCallback, useEffect, useState } from "react";
import { graphql } from "@/gql";
import type { QueuesQuery } from "@/gql/graphql";
import { execute } from "@/lib/graphql";

const QueuesDocument = graphql(`
  query Queues {
    queues {
      name
      messages
      consumers
    }
  }
`);

const PurgeQueueDocument = graphql(`
  mutation PurgeQueue($input: PurgeQueueInput!) {
    purgeQueue(input: $input) {
      unsignedInt
    }
  }
`);

type Queue = QueuesQuery["queues"][number];

/** MQ-5: queue depth and consumers, with purge; platform-admin only (queues.manage). */
export function QueuesPage() {
  const [queues, setQueues] = useState<Queue[] | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  const reload = useCallback(
    () =>
      execute(QueuesDocument).then(
        (d) => setQueues(d.queues),
        (e: Error) => setStatus(e.message),
      ),
    [],
  );
  useEffect(() => {
    void reload();
  }, [reload]);

  async function purge(name: string) {
    if (!confirm(`Remove every ready message from ${name}?`)) return;
    try {
      const { purgeQueue } = await execute(PurgeQueueDocument, { input: { name } });
      setStatus(`Purged ${purgeQueue.unsignedInt ?? 0} messages from ${name}.`);
    } catch (e) {
      setStatus((e as Error).message);
    }
    await reload();
  }

  return (
    <main className="space-y-4 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">Queues</h1>
        <button
          type="button"
          onClick={() => void reload()}
          className="rounded-md border border-border px-3 py-1 text-sm hover:bg-surface"
        >
          Refresh
        </button>
      </div>
      <p className="text-xs text-muted">
        Counts come from the RabbitMQ management API and lag a few seconds.
      </p>
      {status && (
        <p role="status" className="text-sm">
          {status}
        </p>
      )}
      {queues && (
        <table className="w-full max-w-3xl text-sm">
          <thead>
            <tr className="border-b border-border text-left text-muted">
              <th scope="col" className="py-2 font-medium">
                Queue
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Messages
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Consumers
              </th>
              <th scope="col" className="py-2">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {queues.map((q) => (
              <tr key={q.name} className="border-b border-border">
                <td className="py-2 font-mono">{q.name}</td>
                <td className="py-2 text-right tabular-nums">{q.messages}</td>
                <td className="py-2 text-right tabular-nums">{q.consumers}</td>
                <td className="py-2 text-right">
                  <button
                    type="button"
                    onClick={() => void purge(q.name)}
                    className="rounded-md border border-border px-3 py-1 hover:bg-surface"
                  >
                    Purge
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
