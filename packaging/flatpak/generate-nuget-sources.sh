#!/usr/bin/env bash
# Regenerate nuget-sources.json for the Flatpak build.
#
# The Flatpak build sandbox has no network, so every NuGet package has to be declared as a
# manifest source. Run this whenever a PackageReference changes in RDPilot.Client.csproj,
# and commit the result.
#
# Requires: flatpak, the freedesktop 25.08 SDK, and the dotnet10 SDK extension.
#
#   flatpak install -y flathub org.freedesktop.Sdk//25.08 \
#     org.freedesktop.Sdk.Extension.dotnet10//25.08
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

freedesktop_version=25.08
dotnet_version=10
runtime=linux-x64

generator="${script_dir}/.flatpak-dotnet-generator.py"
if [[ ! -f "${generator}" ]]; then
  curl -sSfL -o "${generator}" \
    https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/master/dotnet/flatpak-dotnet-generator.py
fi

# The --runtime value must match the RID used by `dotnet publish` in the manifest, otherwise
# the RID-specific native assets (libSkiaSharp.so, libHarfBuzzSharp.so) are never restored.
python3 "${generator}" \
  --dotnet "${dotnet_version}" \
  --freedesktop "${freedesktop_version}" \
  --runtime "${runtime}" \
  "${script_dir}/nuget-sources.json" \
  "${repo_root}/RDPilot.Client/RDPilot.Client.csproj"

echo "Wrote ${script_dir}/nuget-sources.json"
