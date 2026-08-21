# Nuvio TV (Native) — Tizen .NET / NUI

Native Tizen 5.5 `.tpk` rebuild of the NuvioTV web app (`NuvioTVN01.NuvioTV`,
distinct from the wrapped WGT `NuvioTV001` so both can coexist during
migration). License: GPL-3.0-only; see `native/src/NuvioTV.Tizen/res/fonts/OFL.txt`
and the Licenses & Attributions screen for bundled dependencies.

## Architecture

```
native/
  NuvioTV.Tizen.sln
  Directory.Build.props          # LangVersion 8, deterministic build (Core)
  tools/
    convert-strings.mjs          # res/values*/strings.xml -> res/i18n/*.json
    gen-env.mjs                  # local.properties -> res/config/nuvio.env.json
    tizen-dev.sh                 # sdb connect/install/run helpers
    read-version.mjs             # package.json version -> tizen-manifest.xml
  src/
    NuvioTV.Core/                # netstandard2.0 — data/protocol/sync ports,
                                 # zero Tizen deps, host-testable (net8 tests)
    NuvioTV.Core.Tests/          # xUnit suite (dotnet test NuvioTV.Tizen.sln)
    NuvioTV.Tizen/               # tizen70 NUI app: shell, navigation, input,
                                 # widgets, screens, media, platform services
```

Webapp sources under `js/`, `css/`, `res/` remain the behavioral spec.

## Toolchain setup

1. Tizen Studio (x86_64 via Rosetta) with TV Extensions 5.5 + .NET workload;
   CLI at `~/tizen-studio/tools/ide/bin/tizen`, `sdb` at `~/tizen-studio/tools/sdb`.
2. .NET SDK ≥ 8 at `~/.dotnet/dotnet`.
3. Author certificate profile `nuvio-native` (sideload to Developer-Mode TV).
4. TV: Developer Mode ON, `sdb connect <ip>:26101`.

## Build / deploy

```bash
npm run native:strings   # regenerate i18n JSON after editing res/values*
npm run native:env       # regenerate embedded env config
npm run native:build     # read-version.mjs + dotnet build -c Release (.tpk out)
npm run native:deploy    # bash native/tools/tizen-dev.sh install-run
```

## Device notes

- Target: Samsung QE55Q80T (Tizen 5.5). `sdb shell` output is blocked in this
  dev-mode state, so verification uses `sdb install`/`tizen run` exit codes plus
  visual confirmation; `tizen-dev.sh logs` works when dlog unlocks.
- API7 quirks discovered: `FontClient.AddCustomFontDirectory` only (no per-file),
  no `View.Borderline*`, `View.Orientation` instead of `Rotation`,
  `MediaPlayer` has no track-selection API (lists only), `PlayerBufferingTime`
  is a struct.
