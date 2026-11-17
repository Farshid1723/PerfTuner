#!/bin/bash
# Release build: framework-dependent app + engine (they run on Microsoft .NET 9 Desktop Runtime), the small launcher,
# then the packer appends the compressed app to the launcher as PerfTuner-Portable.exe and PerfTuner-Setup.exe.
# usage: build/release/release.sh <out-dir>
set -e
OUT="$1"; W="$(mktemp -d)"
cd "$(dirname "$0")/../.."
dotnet publish src/PerfTuner.App -c Release -r win-x64 --self-contained false -o "$W/payload"
dotnet publish src/PerfTuner.Service -c Release -r win-x64 --self-contained false -o "$W/payload/Service"
dotnet publish tools/PerfTuner.Bootstrap -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$W/boot"
dotnet run --project tools/PerfTuner.Pack -c Release -- "$W/payload" "$W/boot/PerfTuner-Bootstrap.exe" "$OUT"
rm -rf "$W"
