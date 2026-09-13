import { createRequire } from "node:module";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const expectedVersion = process.env.EXPECTED_SHARP_VERSION ?? "0.35.4";
const webRoot = fileURLToPath(new URL("../../apps/web/", import.meta.url));
const rootRequire = createRequire(import.meta.url);
const nextPackagePath = rootRequire.resolve("next/package.json", {
  paths: [webRoot],
});
const nextRequire = createRequire(nextPackagePath);
const sharpEntryPath = nextRequire.resolve("sharp");
let packageDirectory = dirname(sharpEntryPath);
let sharpPackage;
while (true) {
  try {
    const candidate = JSON.parse(
      await readFile(join(packageDirectory, "package.json"), "utf8"),
    );
    if (candidate.name === "sharp") {
      sharpPackage = candidate;
      break;
    }
  } catch (error) {
    if (error?.code !== "ENOENT") {
      throw error;
    }
  }
  const parent = dirname(packageDirectory);
  if (parent === packageDirectory) {
    throw new Error("SHARP_RUNTIME_PACKAGE_METADATA_NOT_FOUND");
  }
  packageDirectory = parent;
}

if (sharpPackage.version !== expectedVersion) {
  throw new Error(
    `SHARP_RUNTIME_VERSION_MISMATCH: expected=${expectedVersion} actual=${sharpPackage.version}`,
  );
}

const sharp = nextRequire("sharp");
const output = await sharp({
  create: {
    width: 2,
    height: 2,
    channels: 4,
    background: { r: 17, g: 34, b: 51, alpha: 1 },
  },
})
  .png()
  .toBuffer();

const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
if (output.length <= pngSignature.length || !output.subarray(0, 8).equals(pngSignature)) {
  throw new Error("SHARP_RUNTIME_INVALID_PNG_OUTPUT");
}

console.log(
  JSON.stringify({
    result: "SHARP_RUNTIME_SMOKE_PASSED",
    sharp_version: sharpPackage.version,
    dependency_parent: "next",
    node_version: process.versions.node,
    platform: process.platform,
    architecture: process.arch,
    output_format: "png",
    output_bytes: output.length,
  }),
);
