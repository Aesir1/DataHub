import type { Metadata } from "next";
import { brandingCss, googleFontsUrl } from "@/lib/branding";
import { getBranding } from "@/lib/branding.server";
import "./globals.css";

// Branding is read at request time so one build serves any brand (never prerendered with a baked-in brand).
export const dynamic = "force-dynamic";

export function generateMetadata(): Metadata {
  const branding = getBranding();
  return {
    title: { default: branding.name, template: `%s · ${branding.name}` },
    description: branding.authScreen.tagline,
    icons: branding.favicon ? { icon: branding.favicon } : undefined,
  };
}

// Theme choice is a per-browser convenience; runs before paint to avoid a flash.
const themeScript = `try{var t=localStorage.getItem("theme");if(t)document.documentElement.dataset.theme=t}catch(e){}`;

export default function RootLayout({ children }: { children: React.ReactNode }) {
  const branding = getBranding();
  const fonts = googleFontsUrl(branding);
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        {fonts && (
          <>
            <link rel="preconnect" href="https://fonts.googleapis.com" />
            <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="" />
            <link rel="stylesheet" href={fonts} />
          </>
        )}
        <style id="branding">{brandingCss(branding)}</style>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
      </head>
      <body className="min-h-screen antialiased">{children}</body>
    </html>
  );
}
