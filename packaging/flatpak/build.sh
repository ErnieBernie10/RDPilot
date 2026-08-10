#!/bin/sh
# Build and install RDPilot inside the Flatpak sandbox.
#
# Invoked by the `rdpilot` module in io.github.ErnieBernie10.RDPilot.yaml. flatpak-builder
# exports FLATPAK_DEST and FLATPAK_ID into the build environment. The working directory
# holds the pinned git checkout plus the manifest-directory siblings copied in as
# `type: file` sources (this script, rdpilot.sh, the desktop and metainfo files) and the
# ./nuget-sources tree restored from nuget-sources.json.
#
# Keep VERSION below in sync with the git tag/commit pinned in the manifest and with the
# newest <release> in the metainfo. See "Releasing" in README.md.
set -eu

VERSION=1.3.0

# The checked-in NuGet.config points at nuget.org and the Avalonia feed; neither is
# reachable during a Flatpak build. Everything is restored from ./nuget-sources.
rm -f NuGet.config

dotnet publish RDPilot.Client/RDPilot.Client.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    --source ./nuget-sources \
    --source /usr/lib/sdk/dotnet10/nuget/packages \
    -p:Version="${VERSION}" \
    -p:AssemblyVersion="${VERSION}.0" \
    -p:FileVersion="${VERSION}.0" \
    -p:InformationalVersion="${VERSION}" \
    -p:NativeWrapperUseVcpkg=false

# RDPilot.Client.csproj (CopyNativeWrapperToPublish) already dropped libfreerdp_wrapper.so
# into the publish directory next to the managed assembly.
install -d "${FLATPAK_DEST}/lib/rdpilot"
cp -a RDPilot.Client/bin/Release/net10.0/linux-x64/publish/. "${FLATPAK_DEST}/lib/rdpilot/"
install -Dm755 rdpilot.sh "${FLATPAK_DEST}/bin/rdpilot"

install -Dm644 "${FLATPAK_ID}.desktop" \
    "${FLATPAK_DEST}/share/applications/${FLATPAK_ID}.desktop"
install -Dm644 "${FLATPAK_ID}.metainfo.xml" \
    "${FLATPAK_DEST}/share/metainfo/${FLATPAK_ID}.metainfo.xml"
install -Dm644 RDPilot.Client/Assets/rdpilot-app-icon.svg \
    "${FLATPAK_DEST}/share/icons/hicolor/scalable/apps/${FLATPAK_ID}.svg"
install -Dm644 RDPilot.Client/Assets/rdpilot-app-icon-256.png \
    "${FLATPAK_DEST}/share/icons/hicolor/256x256/apps/${FLATPAK_ID}.png"
install -Dm644 LICENSE "${FLATPAK_DEST}/share/licenses/${FLATPAK_ID}/LICENSE"
