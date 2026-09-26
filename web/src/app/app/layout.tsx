import { auth, federatedSignOut } from "@/auth";
import { AppNav, ThemeToggle } from "@/components/AppNav";
import { resolveAsset } from "@/lib/branding";
import { getBranding } from "@/lib/branding.server";

export default async function AppLayout({ children }: { children: React.ReactNode }) {
  const [session, branding] = [await auth(), getBranding()];
  return (
    <div className="flex min-h-screen flex-col">
      <header className="flex flex-wrap items-center gap-4 border-b border-border px-4 py-3">
        <span className="flex items-center gap-2 font-semibold">
          {/* eslint-disable-next-line @next/next/no-img-element -- logo URL comes from runtime branding */}
          <img src={resolveAsset(branding.logo)} alt="" className="h-7 w-7" />
          {branding.name}
        </span>
        <AppNav />
        <form
          className="ml-auto flex items-center gap-3 text-sm"
          action={async () => {
            "use server";
            await federatedSignOut();
          }}
        >
          <span className="text-muted">{session?.user?.email}</span>
          <ThemeToggle />
          <button type="submit" className="rounded-md border border-border px-3 py-1 hover:bg-surface">
            Sign out
          </button>
        </form>
      </header>
      {children}
    </div>
  );
}
