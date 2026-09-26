"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { graphql } from "@/gql";
import { execute } from "@/lib/graphql";

const ViewerDocument = graphql(`
  query Viewer {
    viewer {
      email
      permissions
    }
  }
`);

const links = [
  { href: "/app", label: "Containers", permission: "containers.read" },
  { href: "/app/products", label: "Products", permission: "products.read" },
  { href: "/app/documents", label: "Documents", permission: "documents.read" },
  { href: "/app/admin/queues", label: "Admin · Queues", permission: "queues.manage" },
] as const;

/** Only shows what the caller may use; the Api enforces the same permissions. */
export function AppNav() {
  const pathname = usePathname();
  const [permissions, setPermissions] = useState<string[] | null>(null);

  useEffect(() => {
    execute(ViewerDocument).then(
      (d) => setPermissions(d.viewer.permissions),
      () => setPermissions([]),
    );
  }, []);

  return (
    <nav aria-label="Main" className="flex flex-wrap gap-1">
      {permissions &&
        links
          .filter((l) => permissions.includes(l.permission))
          .map((l) => (
            <Link
              key={l.href}
              href={l.href}
              aria-current={pathname === l.href ? "page" : undefined}
              className="rounded-md px-3 py-1.5 text-sm hover:bg-surface aria-[current=page]:bg-primary aria-[current=page]:text-on-primary"
            >
              {l.label}
            </Link>
          ))}
    </nav>
  );
}

export function ThemeToggle() {
  const toggle = () => {
    const root = document.documentElement;
    const dark = root.dataset.theme
      ? root.dataset.theme === "dark"
      : matchMedia("(prefers-color-scheme: dark)").matches;
    root.dataset.theme = dark ? "light" : "dark";
    try {
      localStorage.setItem("theme", root.dataset.theme);
    } catch {
      // storage blocked: the choice just is not remembered
    }
  };
  return (
    <button
      type="button"
      onClick={toggle}
      className="rounded-md border border-border px-3 py-1 hover:bg-surface"
      aria-label="Toggle dark mode"
    >
      ◐
    </button>
  );
}
