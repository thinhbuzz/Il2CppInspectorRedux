#!/usr/bin/env bun
import AdmZip from "adm-zip";
import { $ } from "bun";
import { mkdir, rm } from "fs/promises";
import { existsSync, rmSync } from "fs";
import { dirname, isAbsolute, join } from "path";
import { parseArgs } from "util";

const { values } = parseArgs({
  args: Bun.argv,
  options: {
    apkm: {
      type: "string",
    },
    baseapk: {
      type: "string",
      default: "base.apk",
    },
    splitapk: {
      type: "string",
      default: "split_config.arm64_v8a.apk",
    },
    help: {
      type: "boolean",
      default: false,
    },
  },
  strict: true,
  allowPositionals: true,
});

const { apkm, baseapk, splitapk, help } = values;

if (help || !apkm || !baseapk || !splitapk) {
  console.log("Usage: bun-builder <apkm> <baseapk> <splitapk>");
  process.exit(0);
}

const apkFolder = isAbsolute(apkm) ? dirname(apkm) : process.cwd();
const tempFolder = join(apkFolder, "temp");
await rm(tempFolder, { recursive: true, force: true });
await mkdir(tempFolder, { recursive: true });
process.on("exit", () => {
  console.log("Cleaning up...");
  rmSync(tempFolder, { recursive: true, force: true });
});

if (!existsSync(apkm)) {
  console.error(`${apkm} not found`);
  process.exit(1);
}

const apkZipFile = new AdmZip(apkm);

console.log(`Extracting base.apk from ${apkm}`);
tryExtractApks({
  zip: apkZipFile,
  zipPath: apkm,
  apks: [baseapk, "com.nianticlabs.pokemongo.apk"],
  targetFolder: tempFolder,
  targetName: "base.apk",
});
console.log(`Extracting split_config.arm64_v8a.apk from ${apkm}`);
tryExtractApks({
  zip: apkZipFile,
  zipPath: apkm,
  apks: [splitapk, "config.arm64_v8a.apk"],
  targetFolder: tempFolder,
  targetName: "split_config.arm64_v8a.apk",
});

const baseApkPath = join(tempFolder, "base.apk");
const splitApkPath = join(tempFolder, "split_config.arm64_v8a.apk");

const baseApkZipFile = new AdmZip(baseApkPath);
console.log("Extracting global-metadata.dat from base.apk");
tryExtractApks({
  zip: baseApkZipFile,
  zipPath: baseApkPath,
  apks: ["assets/bin/Data/Managed/Metadata/global-metadata.dat"],
  targetFolder: apkFolder,
  targetName: "global-metadata.dat",
});

const splitApkZipFile = new AdmZip(splitApkPath);
console.log("Extracting libil2cpp.so from split_config.arm64_v8a.apk");
tryExtractApks({
  zip: splitApkZipFile,
  zipPath: splitApkPath,
  apks: ["lib/arm64-v8a/libil2cpp.so"],
  targetFolder: apkFolder,
  targetName: "libil2cpp.so",
});

console.log("Running Il2CppInspectorRedux in", apkFolder);
await $`$IL2CPP_INSPECTOR -i libil2cpp.so -m global-metadata.dat --select-outputs -d output/DummyDll -o metadata.json -p il2cpp.py -t IDA -l tree -c output/Code`.cwd(
  apkFolder
);
console.log("Done!");

interface TryExtractApksOptions {
  zip: AdmZip;
  zipPath: string;
  apks: string[];
  targetFolder: string;
  targetName: string;
}

function tryExtractApks({
  zip,
  zipPath,
  apks,
  targetFolder,
  targetName,
}: TryExtractApksOptions) {
  for (const apk of apks) {
    try {
      if (
        zip.extractEntryTo(apk, targetFolder, false, true, false, targetName)
      ) {
        return;
      }
    } catch (e) {
      if (e instanceof Error && e.message === "ADM-ZIP: Entry doesn't exist") {
        continue;
      }
      console.error(`Failed to extract ${apk} to ${targetFolder}: ${e}`);
      process.exit(1);
    }
  }
  console.error(`Not found ${apks.join(", ")} in ${zipPath}`);
  process.exit(1);
}
