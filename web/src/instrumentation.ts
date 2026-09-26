import { registerOTel } from "@vercel/otel";

export async function register() {
  // NF-4: traces/metrics to the Aspire dashboard (OTEL_EXPORTER_OTLP_* are injected by the AppHost).
  registerOTel({ serviceName: "web" });

  if (process.env.NEXT_RUNTIME === "nodejs") {
    // FE-10/FE-14: an invalid branding file fails startup; weak contrast only warns.
    const { readBranding, brandingFile } = await import("./lib/branding.server");
    const { contrastWarnings } = await import("./lib/branding");
    let branding;
    try {
      branding = readBranding();
    } catch (error) {
      // Next only logs instrumentation errors and keeps serving; a broken brand must stop the process.
      console.error((error as Error).message);
      process.exit(1);
    }
    for (const warning of contrastWarnings(branding)) console.warn(warning);
    console.info(`branding: loaded ${brandingFile()} (${branding.name})`);
  }
}
