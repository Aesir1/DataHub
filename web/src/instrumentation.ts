import { BatchSpanProcessor } from "@opentelemetry/sdk-trace-base";
import { OTLPHttpProtoTraceExporter, registerOTel } from "@vercel/otel";

export async function register() {
  // NF-4: traces to the Aspire dashboard ("auto": OTEL_EXPORTER_OTLP_* injected by the AppHost) and, when
  // OTEL_COLLECTOR_ENDPOINT is set, also to Alloy for the Grafana stack.
  const collector = process.env.OTEL_COLLECTOR_ENDPOINT?.replace(/\/$/, "");
  registerOTel({
    serviceName: "web",
    attributes: { "service.namespace": "datahub" },
    spanProcessors: collector
      ? ["auto", new BatchSpanProcessor(new OTLPHttpProtoTraceExporter({ url: `${collector}/v1/traces` }))]
      : ["auto"],
  });

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
