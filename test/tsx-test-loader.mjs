import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

import * as vite from "vite";

export async function load(url, context, nextLoad) {
  if (!url.endsWith(".tsx")) {
    return nextLoad(url, context);
  }

  const filename = fileURLToPath(url);
  const source = await readFile(filename, "utf8");

  let code;
  if (typeof vite.transformWithOxc === "function") {
    const transformed = await vite.transformWithOxc(source, filename, {
      lang: "tsx",
      jsx: { runtime: "automatic" },
    });
    code = transformed.code;
  } else if (typeof vite.transformWithEsbuild === "function") {
    const transformed = await vite.transformWithEsbuild(source, filename, {
      loader: "tsx",
      jsx: "automatic",
    });
    code = transformed.code;
  } else {
    code = source;
  }

  return {
    format: "module",
    source: code,
    shortCircuit: true,
  };
}
