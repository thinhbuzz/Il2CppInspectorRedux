#!/usr/bin/env bun
import AdmZip from "adm-zip";
import { $ } from "bun";
import { mkdir, rm } from "fs/promises";
import { rmSync } from "fs";
import { dirname, join } from "path";
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


const apkFolder = dirname(apkm);
const tempFolder = join(apkFolder, "temp");
await rm(tempFolder, { recursive: true, force: true });
await mkdir(tempFolder, { recursive: true });
process.on('exit', () => {
  console.log("Cleaning up...");
  rmSync(tempFolder, { recursive: true, force: true });
});

const apkZipFile = new AdmZip(apkm);

console.log("Extracting base.apk from apkm");
tryExtractApks(apkZipFile, [baseapk, "com.nianticlabs.pokemongo.apk"], tempFolder, "base.apk");
console.log("Extracting split_config.arm64_v8a.apk from apkm");
tryExtractApks(apkZipFile, [splitapk, "config.arm64_v8a.apk"], tempFolder, "split_config.arm64_v8a.apk");

const baseApkPath = join(tempFolder, "base.apk");
const splitApkPath = join(tempFolder, "split_config.arm64_v8a.apk");

const baseApkZipFile = new AdmZip(baseApkPath);
console.log("Extracting global-metadata.dat from base.apk");
tryExtractApks(baseApkZipFile, ["assets/bin/Data/Managed/Metadata/global-metadata.dat"], apkFolder, "global-metadata.dat");

const splitApkZipFile = new AdmZip(splitApkPath);
console.log("Extracting libil2cpp.so from split_config.arm64_v8a.apk");
tryExtractApks(splitApkZipFile, ["lib/arm64-v8a/libil2cpp.so"], apkFolder, "libil2cpp.so");

console.log("Running il2cpp...");
await $`$IL2CPP_INSPECTOR -i libil2cpp.so -m global-metadata.dat --select-outputs -d output/DummyDll -o metadata.json -p il2cpp.py -t IDA -l tree -c output/Code`.cwd(apkFolder);
console.log("Done!");

function tryExtractApks(zip: AdmZip, apks: string[], targetFolder: string, targetName: string): string | undefined | never {
  for (const apk of apks) {
    try {
      if (zip.extractEntryTo(apk, targetFolder, false, true, false, targetName)) {
        return apk;
      }
    } catch(e) {
      if (e instanceof Error && e.message === "ADM-ZIP: Entry doesn't exist") {
        continue;
      }
      console.error(`Failed to extract ${apk} to ${targetFolder}: ${e}`);
      process.exit(1);
    }
  }
  console.error(`Not found ${apks.join(", ")} in ${apkm}`);
  process.exit(1);
}
