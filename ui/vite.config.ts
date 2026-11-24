import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import path from "node:path";

// Two entries, two plugins, two wwwroot directories — never one merged SPA: `chat.read`
// (conversation content) and `admin.*` (operations) are different permission worlds.
//
// The target is selected with `vite build --mode <target>` so the same config stays platform
// independent (no cross-env shim needed on Windows CI).
type Target = "webconsole" | "conversation";

const here = path.dirname(fileURLToPath(import.meta.url));

// Each target owns its own Vite root so the emitted document is literally `index.html` — that is the
// file the HttpListener serves for `/` and for every client-side route.
const targets: Record<Target, { root: string; outDir: string }> = {
  webconsole: {
    root: path.resolve(here, "webconsole-app"),
    outDir: path.resolve(here, "../plugins/Natter.Plugin.WebConsole/wwwroot"),
  },
  conversation: {
    root: path.resolve(here, "conversation-app"),
    outDir: path.resolve(here, "../plugins/Natter.Plugin.ConversationConsole/wwwroot"),
  },
};

export default defineConfig(({ mode }) => {
  const target = (mode in targets ? mode : "webconsole") as Target;
  const selected = targets[target];

  return {
    root: selected.root,
    base: "/",
    plugins: [react()],
    build: {
      outDir: selected.outDir,
      emptyOutDir: true,
      sourcemap: false,
      chunkSizeWarningLimit: 1000,
      target: "es2022",
      assetsDir: "assets",
      // Hashed names give `Cache-Control: immutable` a stable contract; the hashed chunk is the
      // only thing that changes between builds.
      rollupOptions: {
        // The input is renamed to `index` so the emitted document is `index.html` — that is the file
        // the HttpListener serves for `/` and for every SPA fallback route.
        input: { index: path.resolve(selected.root, "index.html") },
        output: {
          entryFileNames: "assets/[name]-[hash].js",
          chunkFileNames: "assets/[name]-[hash].js",
          assetFileNames: "assets/[name]-[hash][extname]",
          // AntD is the one vendor chunk worth splitting explicitly: a console code change then only
          // invalidates the small app chunk. ECharts is already lazy-loaded (LatencyChart chunk).
          manualChunks: (id) => (id.includes("node_modules/antd") || id.includes("node_modules/@ant-design")
            ? "antd"
            : undefined),
        },
      },
    },
    server: {
      port: target === "webconsole" ? 5190 : 5191,
      strictPort: false,
    },
  };
});
