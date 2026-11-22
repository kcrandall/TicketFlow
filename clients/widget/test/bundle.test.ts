import { build } from "esbuild";
import { describe, expect, it } from "vitest";

describe("bundle", () => {
  it("stays within the size budget and ships no source contents in its source map", async () => {
    const result = await build({
      entryPoints: ["src/index.ts"],
      bundle: true,
      format: "iife",
      globalName: "TicketFlowChat",
      target: "es2022",
      minify: true,
      sourcemap: "external",
      sourcesContent: false,
      write: false,
      outdir: "dist-test",
    });
    const js = result.outputFiles.find((file) => file.path.endsWith(".js"))!;
    const map = result.outputFiles.find((file) => file.path.endsWith(".map"))!;
    expect(js.contents.byteLength).toBeLessThan(80 * 1024);
    expect(JSON.parse(map.text).sourcesContent).toBeUndefined();
  });
});
