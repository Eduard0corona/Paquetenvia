import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.e2e.ts"],
    testTimeout: 45_000,
    hookTimeout: 15_000,
    fileParallelism: false,
  },
});
