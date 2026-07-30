import { defineConfig } from "vitest/config";

export default defineConfig({
  base: "./",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: true,
    target: "chrome108",
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
  },
  test: {
    environment: "node",
    include: ["tests/**/*.test.ts"],
  },
});
