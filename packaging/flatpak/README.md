# Flatpak packaging

Source of truth for the Flathub submission. Every file here is a sibling of the manifest, so
this directory can be copied verbatim into the `flathub/io.github.ErnieBernie10.RDPilot`
repository (Flathub expects the manifest at the repo root).

| File | Purpose |
| --- | --- |
| `io.github.ErnieBernie10.RDPilot.yaml` | Flatpak manifest |
| `io.github.ErnieBernie10.RDPilot.metainfo.xml` | AppStream metadata (store page) |
| `io.github.ErnieBernie10.RDPilot.desktop` | Desktop entry |
| `nuget-sources.json` | Offline NuGet sources (generated) |
| `rdpilot.sh` | `/app/bin/rdpilot` launcher |
| `generate-nuget-sources.sh` | Regenerates `nuget-sources.json` |
| `flathub.json` | Restricts Flathub builds to `x86_64` |

`flathub.json` pins the build to `x86_64` because `nuget-sources.json` and the
`dotnet publish` RID are `linux-x64`. Supporting aarch64 means generating a second set of
sources with `--runtime linux-arm64` and selecting the RID per arch.

Icons are installed from the git checkout: `RDPilot.Client/Assets/rdpilot-app-icon.svg` and
`rdpilot-app-icon-256.png`.

### About `nuget-sources.json`

It was bootstrapped on Windows (`dotnet restore -r linux-x64`, then the same hashing the
upstream generator does), so it carries the framework packs for **two** .NET patch levels:

- `10.0.8` — what the Flathub `dotnet10` extension currently ships (SDK 10.0.300)
- `10.0.10` — what a current local SDK resolves

`microsoft.netcore.app.host.linux-x64` is the one that matters: it supplies the apphost for
`--self-contained false -r linux-x64` and is **not** in
`/usr/lib/sdk/dotnet10/nuget/packages`, so it has to come from here at exactly the version
the extension's SDK asks for. If the extension bumps its SDK patch, the build fails with
NU1101 for that package until this file is regenerated.

Extra packages are harmless — they are just downloaded and ignored. Run
`./generate-nuget-sources.sh` on Linux to replace this with the exact set.

## What the manifest builds

1. `dotnet` — installs the .NET 10 runtime from the SDK extension into `/app/lib/dotnet`.
   The app is published framework-dependent, same as the Arch package.
2. `libsecret` — `RDPilot.Client/Services/SecretStore.cs` shells out to `secret-tool` and
   deliberately does not fall back to plaintext, so the binary must exist in the sandbox.
   Paired with `--talk-name=org.freedesktop.secrets`.
3. `cjson`, `freerdp` — FreeRDP 3 is not in the freedesktop runtime. Only `libfreerdp3`,
   `libfreerdp-client3` and `libwinpr3` are built; `RDPilot.Wrapper/CMakeLists.txt` picks
   them up from `/app` through pkg-config with no code changes.
4. `rdpilot` — `dotnet publish`, which also drives the CMake build of
   `libfreerdp_wrapper.so` via the `BuildNativeWrapper` target in `RDPilot.Client.csproj`.

Config and secrets land in `~/.var/app/io.github.ErnieBernie10.RDPilot/config/RDPilot.Client/`
because Flatpak redirects `XDG_CONFIG_HOME`. `AppDataPaths.AppName` stays `RDPilot.Client`;
changing it would orphan saved passwords.

## Build and test locally

```sh
flatpak install -y flathub org.flatpak.Builder \
  org.freedesktop.Platform//25.08 org.freedesktop.Sdk//25.08 \
  org.freedesktop.Sdk.Extension.dotnet10//25.08 \
  org.freedesktop.Platform.ffmpeg-full//25.08

cd packaging/flatpak
flatpak run org.flatpak.Builder --force-clean --user --install \
  --install-deps-from=flathub --ccache \
  --repo=repo build-dir io.github.ErnieBernie10.RDPilot.yaml

flatpak run io.github.ErnieBernie10.RDPilot
```

Lint exactly the way Flathub does — warnings are fatal there:

```sh
flatpak run --command=flatpak-builder-lint org.flatpak.Builder \
  manifest io.github.ErnieBernie10.RDPilot.yaml
flatpak run --command=flatpak-builder-lint org.flatpak.Builder builddir build-dir
```

Two errors are expected outside Flathub and can be ignored locally:

- `appstream-external-screenshot-url`
- `appstream-remote-icon-not-mirrored`

Both assert that the URLs in the composed AppStream catalogue start with
`https://dl.flathub.org/media` — a **prefix** check, not a reachability one. Flathub satisfies
it by running flatpak-builder with `--compose-url-policy=full`, which makes `appstreamcli
compose` rewrite the URLs. Without that flag the original remote URLs survive and both checks
fire. They are documented as never-granted exceptions, so `--exceptions` will not waive them.

CI filters exactly those two and fails on anything else. Note that an unreachable screenshot
URL surfaces as `appstream-missing-screenshots`, which is *not* filtered.

### Building your working tree instead of a pinned commit

The manifest pins a git commit, which is what Flathub requires. To test uncommitted changes,
temporarily replace the `type: git` source of the `rdpilot` module with:

```yaml
      - type: dir
        path: ../..
```

Do not commit that swap.

## Manual test checklist

The build succeeding proves very little; these are the things that actually break under
sandboxing.

1. Window opens and the Inter font renders.
2. Connect to a real RDP host. `DllNotFoundException` means the FreeRDP module or the
   `libfreerdp_wrapper.so` copy failed.
3. Save a connection with a password, quit, relaunch, reconnect. This exercises `secret-tool`
   plus `--talk-name=org.freedesktop.secrets` and is the most likely thing to be broken.
4. Run once under Wayland (selected automatically because `WAYLAND_DISPLAY` is set — see
   `Program.cs`) and once with `flatpak run --env=RDPILOT_USE_WAYLAND=0` to force X11.
   Compare resize, HiDPI scaling and cursor rendering.
5. On a fractionally scaled monitor, confirm the remote desktop is not ~20% oversized.
6. Copy text in both directions.
7. Watch `[PERF_NATIVE]` / `[PERF_UI]` during a window drag. A large regression against a
   native build usually means the `ffmpeg-full` extension is not mounted.

## Releasing

1. Bump `-p:Version` / `-p:AssemblyVersion` / `-p:FileVersion` / `-p:InformationalVersion` in
   the `rdpilot` module and add a matching `<release>` to the metainfo. The newest release
   version and the built version must agree or the linter complains.
2. Re-run `./generate-nuget-sources.sh` if any `PackageReference` changed.
3. Tag the release, then point the `rdpilot` module's git source at the tag and its commit.
4. Copy this directory into the Flathub repo and open a PR.
