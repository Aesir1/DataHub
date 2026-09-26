import "server-only";
import { readFileSync } from "node:fs";
import path from "node:path";
import { cache } from "react";
import { parseBranding, type Branding } from "./branding";

export function brandingFile(): string {
  return path.resolve(process.cwd(), process.env.BRANDING_FILE ?? "config/branding.json");
}

/** FE-10: read at runtime (one build serves any brand); throws naming the file and the offending path. */
export function readBranding(file = brandingFile()): Branding {
  return parseBranding(JSON.parse(readFileSync(file, "utf8")), file);
}

export const getBranding = cache((): Branding => readBranding());
