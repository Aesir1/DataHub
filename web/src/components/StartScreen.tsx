import type { Branding } from "@/lib/branding";
import { resolveAsset } from "@/lib/branding";

const justify = { left: "justify-start", center: "justify-center", right: "justify-end" } as const;

/** FE-13: branded login/register screen. The form action comes from the page so this stays testable. */
export function StartScreen({ branding, action }: { branding: Branding; action: () => Promise<void> }) {
  const { background, overlay, cardPosition, tagline } = branding.authScreen;
  const backgroundStyle =
    background.type === "image"
      ? {
          backgroundImage: `url("${resolveAsset(background.src)}")`,
          backgroundSize: "cover",
          backgroundPosition: "center",
        }
      : { backgroundColor: background.src };

  return (
    <main
      className="relative flex min-h-screen items-center p-4 md:p-12"
      style={backgroundStyle}
      data-testid="start-screen"
    >
      <div
        aria-hidden
        className="absolute inset-0"
        style={{ backgroundColor: overlay.color, opacity: overlay.opacity }}
      />
      <div className={`relative flex w-full ${justify[cardPosition]}`}>
        <div className="w-full max-w-sm rounded-lg border border-border bg-background p-8 text-center shadow-xl">
          {/* eslint-disable-next-line @next/next/no-img-element -- logo URL comes from runtime branding */}
          <img src={resolveAsset(branding.logo)} alt="" className="mx-auto h-12 w-12" />
          <h1 className="mt-4 text-2xl font-semibold">{branding.name}</h1>
          {tagline && <p className="mt-2 text-sm text-muted">{tagline}</p>}
          <form className="mt-6 space-y-3" action={action}>
            <button
              type="submit"
              className="w-full rounded-md bg-primary px-4 py-2 font-medium text-on-primary hover:opacity-90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
            >
              Sign in or register
            </button>
          </form>
        </div>
      </div>
    </main>
  );
}
