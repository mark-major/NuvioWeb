# NuvioTV Native Tizen .NET (NUI) Rewrite — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the NuvioTV web app as a native Tizen .NET (C# / NUI) `.tpk` that runs on a Samsung Tizen 5.5 TV, replacing the wrapped webapp for Tizen while the web codebase remains the source of truth for webOS/hosted web.

**Architecture:** A headless, host-testable C# core (`NuvioTV.Core`, netstandard2.0) ports every data/protocol/sync module 1:1 from `js/core` + `js/data` + `js/domain`; a NUI app (`NuvioTV.Tizen`, tizen70) provides the shell, navigation, focus engine, widgets, screens, and playback (Tizen.Multimedia.Player). The webapp is *not modified* except: new `native/` tree, new i18n keys, and two npm scripts.

**Tech Stack:** Tizen.NET.API7 `7.0.0.15162` (TFM `tizen70`, Tizen 5.5 M3, on-device .NET Core 3.0 CoreCLR), Tizen.NUI + Tizen.NUI.Components (FlexibleView, FocusManager), Tizen.Multimedia.Player, System.Text.Json, xUnit (host, net8.0), Tizen Studio CLI under Rosetta 2 on macOS + `sdb` to the physical TV.

**Spec:** This plan is self-contained; the webapp at repo root is the behavioral spec. Full research base: agent reports `agent://RuntimeBoot`, `agent://DomainData`, `agent://UIScreenMap`, `agent://PlaybackPlatform`, `agent://I18nAssetsBuild`, `agent://NuiStackResearch` (in this session's transcript).

## Global Constraints

- Target device: **one physical Samsung Tizen 5.5 TV** (no TV emulator exists on Apple Silicon — all UI verification is on-device via `sdb`).
- TFMs: `NuvioTV.Core` → `netstandard2.0`; `NuvioTV.Core.Tests` → `net8.0`; `NuvioTV.Tizen` → `tizen70`. **C# language version ≤ 8.0** (`LangVersion 8.0` in `Directory.Build.props`); no records, no init-only setters, no `System.Text.Json` source generators (runtime is .NET Core 3.0).
- NuGet dependencies allowed (nothing else): `Tizen.NET.API7`, `Tizen.NET.Sdk`, `System.Text.Json` (6.0.x, netstandard2.0-compatible), `Net.Codecrete.QrCodeGenerator` (verify MIT when adding), test deps (`xunit`, `Microsoft.NET.Test.Sdk`). All must be GPL-3.0-compatible (MIT/Apache/BSD) — recorded in the attributions screen.
- Backend contracts are frozen: Supabase RPC names/params, Stremio addon URL shapes, Trakt/Simkl/debrid/TMDB endpoints must match `js/data` + `js/core` byte-for-byte at the JSON layer (explicit `[JsonPropertyName]` everywhere; golden-fixture tests).
- Persistence keys and shapes mirror the localStorage inventory in Appendix B (same key names, same JSON envelopes, same caps) so sync semantics and future tooling stay identical.
- i18n source of truth stays `res/values*/strings.xml`; a converter generates `native/.../Resources/i18n/*.json`. Locale resolution and interpolation semantics (`{{name}}`, `%1$s`/`%1$d`, `%s`) replicate `js/i18n/index.js`.
- **Deferred by user decision (v1):** P2P/EngineFS torrent streaming (debrid covers torrents; seam left via `IStreamingServerResolver`) and YouTube trailer playback (surfaces removed, poster art shown instead).
- License: native port is a derivative of a GPL-3.0-only work → GPL-3.0-only, with attributions screen covering bundled deps.
- Design space: fixed **1920×1080 logical pixels**; all CSS clamp()/dp×2 constants are resolved to fixed px per Appendix C.
- The webapp keeps working at every commit; native work never edits `js/` except the i18n key additions in Task 7.2 and `package.json` scripts in Task 1.1.
- Native package identity: package id `NuvioTVN01`, app id `NuvioTVN01.NuvioTV`, label "Nuvio TV (Native)" — distinct from the WGT (`NuvioTV001`) so both can coexist during migration. Version mirrors `package.json`.
- Commit convention: Conventional Commits (`feat(native): ...`); every task ends with a green host test run (`dotnet test native/NuvioTV.Tizen.sln`) and, for device tasks, a smoke flow on the TV.

---

## Target Architecture

```
native/
  NuvioTV.Tizen.sln
  Directory.Build.props          # LangVersion 8, nullable disable, deterministic build
  tools/
    convert-strings.mjs          # res/values*/strings.xml -> Resources/i18n/*.json
    gen-env.mjs                  # local.properties -> Resources/config/nuvio.env.json
    tizen-dev.sh                 # sdb connect/install/run/dlog helpers
    read-version.mjs             # package.json version -> tizen-manifest.xml
  src/
    NuvioTV.Core/                # netstandard2.0, zero Tizen deps, host-testable
      Models/  Networking/  Storage/  Auth/  Profiles/  Addons/
      Sync/  Integrations/ (Trakt Simkl Debrid Tmdb Ratings IntroDb ParentalGuide MdbList)
      Streams/  Settings/  Update/  I18n/  Diagnostics/
    NuvioTV.Core.Tests/          # net8.0; golden JSON fixtures in Fixtures/
    NuvioTV.Tizen/               # tizen70 NUI app
      NuiFoundation/  Navigation/  Input/  Widgets/  Screens/
      Platform/  Media/  Resources/ (i18n/ config/ fonts/ icons/ themes/)
```

### Port map (web → native)

| Web (source of truth) | Native target | Notes |
|---|---|---|
| `js/config.js` + `nuvio.env.js` | `Core/Configuration/AppConfig.cs` | 24 keys, embedded `nuvio.env.json` |
| `js/core/network/*` | `Core/Networking/NuvioHttpClient.cs` etc. | retry/bearer/401-refresh semantics |
| `js/core/storage/*` + localStorage | `Core/Storage/*` + `Tizen/Platform/JsonFileStore.cs` | same keys/shapes/caps (Appendix B) |
| `js/core/auth/*` | `Core/Auth/*` | Supabase QR device flow, tokens, registration |
| `js/data/repository/*` (addon/meta/stream/subtitle) | `Core/Addons/*` | Stremio protocol verbatim |
| `js/core/profile/*SyncService.js` ×10 + `startupSyncService` | `Core/Sync/*` | 120s cycle, 3-way merge, backoff |
| Trakt/Simkl/debrid/TMDB/etc. clients | `Core/Integrations/*` | endpoints verbatim |
| `js/core/streams/*` | `Core/Streams/*` | badges, autoplay, template DSL |
| `js/i18n/index.js` + res XML | `Core/I18n/I18n.cs` + generated JSON | same fallback chain/aliases |
| `js/ui/navigation/router.js` | `Tizen/Navigation/Router.cs` | pure stack, no History API |
| `js/ui/navigation/focusEngine.js` + `screen.js` + `sharedKeys.js` | `Tizen/Input/*` | key map + spatial algorithm ported verbatim |
| `js/ui/theme/*` + CSS vars | `Tizen/NuiFoundation/Theme*` | 7 palettes × 12 tokens + AMOLED |
| `renderAppShell.js` + 25 `.screen` divs | `Tizen/Screens/*` (View per route) | route enum, view stack |
| `js/core/player/playerController.js` + engines | `Tizen/Media/PlayerSession.cs` | single native engine (AVPlay-era Player) |
| hls.js/dashjs/nativeVideo/avplay engines | `Tizen.Multimedia.Player` | native HLS/DASH/progressive |
| `bitmapSubtitleDecoder.js` (libbitsub WASM) | `Tizen/Media/Subtitles/PgsVobSubDecoder.cs` | pure C# PGS/VobSub → RGBA |
| `subtitleCueLayout.js` + text track rendering | `Tizen/Media/Subtitles/TextSubtitleRenderer.cs` | SRT/VTT/ASS → NUI overlay |
| `localMedia*Repository.js` | dropped; Player native track APIs | embedded track enumeration native |
| `p2p/tizenStreamingServerResolver.js` | `Core/Streams/UnavailableStreamingServerResolver.cs` | seam for future native engine |
| `boot-guard.js` | `Tizen/NuiFoundation/BootGuard.cs` | compat gate + fatal overlay + staged labels |
| `qrcode-generator.js` | `QrCodeGenerator` NuGet + PixelBuffer renderer | see Task 11.1 fallback |
| WGT packaging (`package-tizen.mjs`) | `tizen-manifest.xml` + `tizen package` | privileges map in Task 19.1 |

---

## Phase 0 — Toolchain bring-up (macOS arm64 → Tizen 5.5 TV)

### Task 0.1: Install Tizen toolchain, connect the TV

**Files:** none in repo (machine setup; document in `native/README.md` later at Task 19.2).

- [ ] **Step 1: Install Tizen Studio with TV + .NET extensions under Rosetta**
  - Download Tizen Studio installer (needs x86_64 Java; run under Rosetta — `softwareupdate --install-rosetta` if missing).
  - Install packages: `Tizen SDK Tools > Certificate Manager, CLI + SDB`, `TV Extensions > 5.5`, `.NET` extension if listed.
- [ ] **Step 2: Enable Developer Mode on the TV** — Apps screen, remote keys `1 2 3 4 5`, toggle Developer Mode ON, reboot TV, note the TV's IP; then `sdb connect <tv-ip>:26101` and verify with `sdb devices`.
- [ ] **Step 3: Create an author profile** — Certificate Manager → author certificate (.p12). For sideloading to a Developer-Mode TV a locally generated author cert is sufficient; record the profile name `nuvio-native`.
- [ ] **Step 4: Record device facts** — `sdb shell lsb_release -a` (confirm Tizen 5.5), `sdb shell cat /etc/info.ini` if present; save output to `native/notes/device.md` (gitignored) — API level evidence for the constraint above.

### Task 0.2: Hello-world NUI `.tpk` on the TV (feasibility proof)

**Files:** Create: `native/notes/hello.md` (log of commands + result). Throwaway project outside the repo or under `native/spike/` (deleted after).

- [ ] **Step 1:** `tizen create cs-project -t Tizen.NUI.Template55.Single -v tizen-5.5 -n HelloNuvio` (if the 5.5 template is absent from current Studio, take `Tizen.NUI.Template` at the 5.5 API version and set `<TargetFramework>tizen70</TargetFramework>`).
- [ ] **Step 2:** `tizen build-cs -s nuvio-native` → `.tpk` produced.
- [ ] **Step 3:** `sdb install <tpk>`; `tizen run -p <appid>`; confirm the NUI label renders on the TV; capture `sdb dlog | grep -i nui` tail into `native/notes/hello.md`.
- [ ] **Step 4:** Spike-check the two device APIs the plan leans on but reports marked unverified — in the hello app, print to dlog: `new Tizen.Multimedia.Player().State`, `Tizen.NUI.PixelBuffer` type presence (via `Type.GetType("Tizen.NUI.PixelBuffer")` or direct use), and `Tizen.System.Information.TryGetValue("tizen.org/feature/screen.width", ...)`. Record results in `native/notes/hello.md` (drives Task 11.1 QR path and Task 15 renderer).
- [ ] **Step 5:** Delete the spike project; commit only `native/notes/`.

## Phase 1 — Solution scaffold

### Task 1.1: Solution, projects, props, scripts

**Files:** Create: `native/NuvioTV.Tizen.sln`, `native/Directory.Build.props`, `native/src/NuvioTV.Core/NuvioTV.Core.csproj`, `native/src/NuvioTV.Core.Tests/NuvioTV.Core.Tests.csproj`, `native/src/NuvioTV.Tizen/NuvioTV.Tizen.csproj`, `native/.gitignore`. Modify: `package.json` (scripts only).

**Interfaces (produced):**
- `NuvioTV.Core`: netstandard2.0, root namespace `NuvioTV.Core`.
- `NuvioTV.Tizen`: `Tizen.NET.Sdk`, `tizen70`, `TizenCreateTpkOnBuild=true`, references `NuvioTV.Core`.
- Scripts: `"native:strings": "node native/tools/convert-strings.mjs"`, `"native:env": "node native/tools/gen-env.mjs"`, `"native:build": "node native/tools/read-version.mjs && dotnet build native/src/NuvioTV.Tizen -c Release"`, `"native:deploy": "bash native/tools/tizen-dev.sh install-run"`.

- [ ] **Step 1:** `Directory.Build.props`: `<LangVersion>8.0</LangVersion><Nullable>disable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><InvariantGlobalization>true</InvariantGlobalization>` (Core only via condition to not fight Tizen tooling).
- [ ] **Step 2:** Write failing test `SmokeTests.cs`: `Assert.Equal("nuvio", CoreInfo.Name)` referencing a stub `CoreInfo` — run `dotnet test`, see it fail, implement, see it pass. (Establishes the red/green loop for all Core tasks.)
- [ ] **Step 3:** Add the four npm scripts (tools created later — scripts may fail until Tasks 7.1/19.1; that's fine, they are invoked only there).
- [ ] **Step 4:** Commit `chore(native): scaffold solution`.

### Task 1.2: Configuration

**Files:** Create: `native/tools/gen-env.mjs`, `Core/Configuration/AppConfig.cs`, `Core/Configuration/CoreInfo.cs`; Test: `ConfigurationTests.cs`.

**Interfaces:** `AppConfig` static class exposing exactly: `SupabaseUrl, SupabaseAnonKey, SupabaseFallbackUrl, TvLoginWebBaseUrl, YoutubeProxyUrl, ParentalGuideApiUrl, IntroDbApiUrl, ImdbRatingsApiBaseUrl, ImdbTapframeApiBaseUrl, MdbListApiBaseUrl, AvatarPublicBaseUrl, UniqueContributionsBaseUrl, DonationsBaseUrl, DonationsDonateUrl, SponsorNames, TmdbApiKey, TraktClientId, TraktClientSecret, TraktApiUrl, TraktRedirectUri, SimklClientId, SimklApiUrl, SimklAppName, PremiumizeClientId` + `AppVersion`.

- [ ] **Step 1:** `gen-env.mjs` — import `readEnvProperties` from `scripts/envProperties.mjs` (reuse, don't duplicate), emit `Resources/config/nuvio.env.json` with the same 19 keys + defaults. Keys not in the properties file fall back to `js/config.js` defaults — copy those default literals verbatim from `js/config.js`.
- [ ] **Step 2:** Failing test: `AppConfig.Load` from an embedded fixture JSON sets every key with expected values; missing keys fall back to `js/config.js` defaults. Then implement `AppConfig.Load(Stream)` (System.Text.Json, `JsonPropertyName` camelCase per key) + `AppConfig.AppVersion` from assembly informational version (set by `read-version.mjs` → `AssemblyInfo` in the Tizen project).
- [ ] **Step 3:** `dotnet test` green; commit `feat(native): app configuration`.

## Phase 2 — Core foundations

### Task 2.1: Domain models

**Files:** Create: `Core/Models/*.cs` — `Addon.cs`, `Meta.cs`, `StreamItem.cs`, `SubtitleItem.cs`, `CatalogRow.cs`, `UserProfile.cs`, `WatchProgress.cs`, `ContentTypes.cs` (`Movie/Series/Tv/Channel/Anime` as static class of const strings matching JS values), `LibraryEntry.cs`, `ProfileEnvelope.cs`. Test: `ModelsTests.cs`.

**Interfaces:** C# classes mirroring the factory outputs from `js/domain/model/*` exactly: `Addon{Id,Name,DisplayName,Version,Description,Logo,BaseUrl,Catalogs[],Types[],RawTypes[],Resources[]}`, `Meta{Id,Type,Name,Poster,Background,Logo,Description,Genres[],Videos[],ReleaseInfo}`, `StreamItem{Name,Title,Description,Url,YtId,InfoHash,FileIdx,ExternalUrl,BehaviorHints,AddonName,AddonLogo, Sources[], Quality, QualityValue, ClientResolve, DebridCacheStatus, Subtitles[]}`, `CatalogRow{AddonId,AddonName,AddonBaseUrl,CatalogId,CatalogName,ApiType,Items,IsLoading,HasMore,CurrentPage,SupportsSkip}`, `UserProfile{Id,ProfileIndex,Name,AvatarColorHex,IsPrimary,UsesPrimaryAddons,UsesPrimaryPlugins,AvatarId,AvatarUrl}`, `WatchProgress{ContentId,ContentType,VideoId,PositionMs,DurationMs,UpdatedAt}` + constants `ResumeMinRatio=0.02`, `WatchedThresholdRatio=0.90`.

- [ ] **Step 1:** Write `ModelsTests` — serialize each model to JSON and compare against golden fixtures hand-transcribed from the JS factories (camelCase property names via `[JsonPropertyName]`).
- [ ] **Step 2:** Run → fail. Implement models. Run → green.
- [ ] **Step 3:** Commit `feat(native): domain models`.

### Task 2.2: Networking layer

**Files:** Create: `Core/Networking/NuvioHttpClient.cs`, `NetworkResult.cs`, `MapWithConcurrency.cs`, `SafeApiCall.cs`. Test: `NetworkingTests.cs` with a stub `HttpMessageHandler`.

**Interfaces:**
```csharp
public sealed class NuvioHttpClient {
    public NuvioHttpClient(HttpClient inner, ISessionTokenProvider tokens);
    Task<T> GetJsonAsync<T>(string url, CancellationToken ct = default);
    Task<T> PostJsonAsync<T>(string url, object body, CancellationToken ct = default);
    // mirrors js/core/network/httpClient.js:
    //  - injects Bearer when IncludeSessionAuth set on request
    //  - pre-refreshes expiring JWT (decode payload exp, 30s leeway) via tokens.TryRefreshAsync()
    //  - single retry on 401 after forced refresh
    //  - JSON parse; 204 -> default; throws NuvioHttpException{Status,Code,Detail}
}
public sealed class NetworkResult<T> { public string Status; /* loading|success|error */ public T Data; public string Message; public string Code; }
public static class MapWithConcurrency { public static Task<IReadOnlyList<TOut>> RunAsync<TIn,TOut>(int concurrency, IEnumerable<TIn> items, Func<TIn,CancellationToken,Task<TOut>> map, CancellationToken ct); }
```

- [ ] **Step 1:** Failing tests: (a) 401-then-refresh-then-success performs exactly 2 calls; (b) expiring-token pre-refresh within 30s leeway; (c) 204 → null; (d) concurrency=4 caps in-flight (count via Interlocked in stub handler); (e) error surfaces `NuvioHttpException.Status`.
- [ ] **Step 2:** Implement; green; commit `feat(native): http client with auth refresh`.

### Task 2.3: Storage layer

**Files:** Create: `Core/Storage/IKeyValueStore.cs`, `LocalStore.cs`, `SessionStore.cs`, `ProfileScopedStore.cs`, `StorageCaps.cs`; `Tizen/Platform/JsonFileStore.cs` (app project). Test: `StorageTests.cs` (in-memory store).

**Interfaces:**
```csharp
public interface IKeyValueStore { Task<string> GetAsync(string key); Task SetAsync(string key, string json); Task RemoveAsync(string key); }
public sealed class InMemoryKeyValueStore : IKeyValueStore { }   // tests
// Tizen/Platform/JsonFileStore.cs: one file per key under <app-data>/store/, atomic write (tmp+rename), read-through memory cache, debounced flush (250ms)
public static class LocalStore { /* JSON (de)serialize over IKeyValueStore; Get<T>,Set,Remove */ }
public static class SessionStore { /* keys: access_token, refresh_token, is_anonymous_session */ }
public sealed class ProfileScopedStore<T> {
    // port of js/data/local/profileScopedStore.js: envelope {__profileScoped:true,version:1,profiles:{pid:value}}, legacy-migration, normalize+merge on read
    public static ProfileScopedStore<T> Create(string key, Func<T,T> normalize, Func<T,T,T> merge);
    Task<T> GetAsync(string profileId); Task SetAsync(string profileId, T value); Task Subscribe(Action onChanged);
}
```

- [ ] **Step 1:** Failing tests: envelope wrap/unwrap, legacy unscoped value migration, per-profile isolation, cap enforcement helper (`StorageCaps.Enforce(items, cap, by => by.UpdatedAt)` keeps newest — caps: watchProgress 5000, watchedItems 5000, savedLibrary 1000, streamPreferences 500, homeImageCache 500/30d).
- [ ] **Step 2:** Implement; green; commit `feat(native): persistent stores`.

## Phase 3 — Auth + Supabase

### Task 3.1: Supabase client

**Files:** Create: `Core/Auth/SupabaseClient.cs`. Test: `SupabaseClientTests.cs` + `Fixtures/supabase/*.json`.

**Interfaces:** `Task<JsonElement> RpcAsync(string fn, object payload, CancellationToken)` (POST `/rest/v1/rpc/{fn}`, headers `apikey` + session Bearer, `Prefer: resolution=merge-duplicates` on upserts), `Task<IReadOnlyList<T>> TableAsync<T>(string table, string query)`, `Task UpsertAsync(string table, object rows)`; auth endpoints: `SignupAnonymousAsync`, `PasswordTokenAsync`, `RefreshTokenAsync`, `TvLoginsExchangeAsync` (`/functions/v1/tv-logins-exchange`); behavior: primary→fallback URL failover, retry on 408/5xx/520–530 + Cloudflare-style HTML body (port `supabaseAuthFetch.js` logic exactly).

- [ ] **Step 1:** Golden tests: request URL/headers/body JSON strings match fixtures captured from the JS code paths (read `js/core/auth/supabaseAuthFetch.js` + `js/data/remote/supabase/supabaseApi.js` while writing fixtures).
- [ ] **Step 2:** Implement; green; commit.

### Task 3.2: AuthManager + QR login service

**Files:** Create: `Core/Auth/AuthManager.cs`, `AuthState.cs`, `QrLoginService.cs`, `ISessionTokenProvider.cs` (impl in AuthManager). Test: `AuthManagerTests.cs`.

**Interfaces:** port `js/core/auth/authManager.js` + `qrLoginService.js` 1:1 — states `Loading/SignedOut/Authenticated`; `Subscribe(Action<AuthState>)` (returns `IDisposable`, fires immediately); `BootstrapAsync()`, `SignInWithEmailAsync`, `SignOutAsync()`, `RefreshSessionIfNeededAsync()`, `GetEffectiveUserIdAsync()` (RPC `get_sync_owner`); `QrLoginService`: `StartTvLoginSessionAsync(deviceNonce, redirectBaseUrl, deviceName)` → `PollTvLoginSessionAsync(code, nonce)` → `ExchangeTvLoginSessionAsync(code, nonce)` (legacy-signature retry preserved), writing tokens into `SessionStore`.

- [ ] **Step 1:** Failing tests: state machine transitions; poll → exchange → tokens persisted; transient network error tolerance during refresh (from `authManager.js`).
- [ ] **Step 2:** Implement; green; commit `feat(native): auth + qr device login`.

### Task 3.3: Device session registration

**Files:** Create: `Core/Auth/DeviceSessionRegistration.cs`, `Core/Auth/IDeviceMetadata.cs`; `Tizen/Platform/TizenDeviceMetadata.cs`. Test: `DeviceRegistrationTests.cs`.

**Interfaces:** `StartAsync()` registers via RPC `register_current_device` every 15 min with `IDeviceMetadata` payload; `TizenDeviceMetadata` fills: platform `tizen-native`, model/firmware via `Tizen.System.Information`, install id persisted under key `nuvio_web_installation_id` (name kept for backend continuity).

- [ ] **Step 1:** Test: RPC name + payload keys (fixture), interval scheduling (virtual clock). Step 2: implement; green; commit.

## Phase 4 — Stremio addon protocol

### Task 4.1: Addon HTTP client

**Files:** Create: `Core/Addons/StremioAddonClient.cs`, `AddonUrlBuilder.cs`, `AddonCanonicalizer.cs`. Test: `AddonProtocolTests.cs`.

**Interfaces:** URL builders for `manifest.json`, `catalog/{type}/{id}.json`, `catalog/.../skip={n}.json`, `catalog/.../search={q}&genre={g}.json`, `meta/{type}/{id}.json`, `stream/{type}/{vid}.json`, `subtitles/{type}/{id}.json` (+`videoHash`/`videoSize`/`filename` extras); base-query preservation; canonicalizer strips `/manifest.json` and maps `cinemeta-v3` → `v3-cinemeta.strem.io`; defaults `v3-cinemeta.strem.io` + `opensubtitles-v3.strem.io` with builtin fallback manifest (copy the builtin JSON verbatim from `js/data/repository/addonRepository.js`).

- [ ] **Step 1:** Failing URL-builder tests (each shape, plus skip/search/genre combos and the canonicalizer cases). Step 2: implement; green; commit.

### Task 4.2: Addon repository

**Files:** Create: `Core/Addons/AddonRepository.cs`, `AddonManifest.cs`. Test: `AddonRepositoryTests.cs`.

**Interfaces:** port `js/data/repository/addonRepository.js` (796 LOC): `AddAddonAsync(url)`, `RemoveAddonAsync`, `SetAddonOrderAsync`, `SetEnabledAsync`, `SetDisplayNameAsync`; persistence keys `installedAddonUrls`, `installedAddonDisplayNames`, `installedAddonEnabledStates`; manifest cache + in-flight dedupe + builtin cinemeta fallback; resource/type/idPrefix matching and type recovery; `OnInstalledAddonsChanged` event; profile scoping (primary vs per-profile).

- [ ] **Step 1:** Failing tests: install→persist→list; disabled addon excluded from catalog assembly; manifest fetch failure falls back to builtin; duplicate concurrent manifest fetches dedupe (count stub calls). Step 2: implement; green; commit.

### Task 4.3: Catalog/meta/stream/subtitle repositories + home catalogs

**Files:** Create: `Core/Addons/CatalogRepository.cs`, `MetaRepository.cs`, `StreamRepository.cs`, `SubtitleRepository.cs`, `HomeCatalogs.cs`, `Core/Media/AddonLogoCacheKeys.cs` (storage keys only; rendering later). Test: `RepositoriesTests.cs`.

**Interfaces:** `CatalogRepository.GetRowAsync(addon, type, catalogId, skip, extraSearchParams)` → `CatalogRow`; parallel stream resolution across addons (`MapWithConcurrency`, match JS concurrency) incl. meta inline-stream fallback; `HomeCatalogs.ResolveHomeRows(installedAddons)` = ordered union of catalogs without required extras (port `js/core/addons/homeCatalogs.js`).

- [ ] **Step 1:** Failing tests: row assembly, skip paging flags, parallel stream grouping (`{addonId, addonName, streams[]}` grouping shape). Step 2: implement; green; commit `feat(native): stremio repositories`.

## Phase 5 — Integrations (all host-tested with fixture handlers)

### Task 5.1: Trakt

**Files:** `Core/Integrations/Trakt/TraktClient.cs`, `TraktAuthService.cs` (device-code flow), `TraktListsService.cs`. Tests incl. golden payloads (header `trakt-api-version: 2`, `Content-Type: application/json`, pagination via `X-Pagination-Page-Count`); endpoints: `oauth/device/code|token|revoke`, `sync/watchlist(/remove)`, `sync/history(/remove)`, `sync/playback`, `sync/watched/shows`, `users/me/lists[/{id}/items/{movie|show}]`, `users/me/watched/movies`, `shows/{id}/progress/watched`, `users/{id}/stats` — verbatim from `js/data/repository/*trakt*` + `traktAuthService.js`. Tokens stored per profile (`traktAuthState` envelope). Commit.

### Task 5.2: Simkl

**Files:** `Core/Integrations/Simkl/SimklClient.cs`, `SimklAuthService.cs`. Endpoints: `oauth/pin`, `sync/all-items/{shows|movies|anime}?extended=full_anime_seasons`, `sync/activities`, `sync/playback`, `scrobble/{start|pause|stop}`; client-side rate limit (GET 100ms / write 1s) + 5× backoff. Commit.

### Task 5.3: Debrid stack

**Files:** `Core/Integrations/Debrid/TorBoxClient.cs`, `PremiumizeClient.cs`, `RealDebridClient.cs`, `DebridProviders.cs`, `DirectDebridResolver.cs`, `DirectDebridStreamPreparer.cs`, `LocalDebridAvailabilityService.cs`, `DebridFileSelection.cs`, `DebridDeviceAuthService.cs`. Port from `js/core/debrid/*` + respective repositories: TorBox (`api.torbox.app/v1/api`: device auth, torrents/createtorrent FormData, checkcached, mylist, requestdl), Premiumize (`api/transfer/directdl`, `api/cache/check`, `api/item/listall|details`), RD (`/rest/1.0`: addMagnet, info, selectFiles, delete, unrestrict/link — hidden in UI, keep hidden). Resolver: create → season/episode file select → requestdl → 15-min cache. Preparer: pre-resolve top-5 with 6/min–30/hr budget. Tests: resolver state machine incl. cache TTL, file selection heuristics, budget limiter. Commit `feat(native): debrid providers`.

### Task 5.4: Metadata/extras clients

**Files:** `Core/Integrations/Tmdb/TmdbClient.cs` + `TmdbMetadataService.cs` (find/external_ids; enrichment incl. cast), `Core/Integrations/Ratings/ImdbEpisodeRatingsRepository.cs` (tapframe + fallback), `MdbListRepository.cs` (`/rating/{movie|show}/{provider}`), `Core/Integrations/IntroDbClient.cs` (`/segments`), `ParentalGuideRepository.cs` (tiffara parentsGuide). Port verbatim; fixture tests. Commit.

## Phase 6 — Streams + sync

### Task 6.1: Stream presentation & selection (pure logic — heavily tested)

**Files:** `Core/Streams/StreamBadgeRules.cs`, `StreamDisplayText.cs`, `StreamResumeIdentity.cs`, `ReleaseToken.cs`, `StreamAutoPlaySelector.cs`, `DebridStreamTemplateEngine.cs`, `IStreamingServerResolver.cs` + `UnavailableStreamingServerResolver.cs`. Port `js/core/streams/*` + `debridStreamTemplateEngine.js` (Android template DSL for names/descriptions). Tests: badge matrix (quality/codec/cache/4K flags), autoplay scoring (`selectBestStreamCandidate` from playerScreen port: same weights), template DSL cases from fixtures. `IStreamingServerResolver.TryResolveAsync(infoHash, season, episode)` returns null in v1 → stream rows with `InfoHash != null && Url == null` and no debrid resolution render as unavailable (Task 12.3). Commit.

### Task 6.2: Sync services (the cloud loop)

**Files:** `Core/Sync/StartupSyncService.cs`, `SyncClientIdentity.cs`, `ProfileManager.cs`, `ProfileSyncService.cs`, `WatchProgressSyncService.cs`, `WatchedItemsSyncService.cs`, `SavedLibrarySyncService.cs`, `HomeCatalogSettingsSyncService.cs`, `ProviderCredentialSyncService.cs`, `TraktCredentialSyncService.cs`, `SimklCredentialSyncService.cs`, `LibrarySyncService.cs` (addons), `PluginSyncService.cs`, `CollectionSyncService.cs`, `ProfileSettingsSyncService.cs` (largest — port model + pull/push in slices, tests per settings group). RPC names/payloads verbatim (`sync_pull_*`, `sync_push_*`, table fallbacks `tv_addons`/`addons` on PGRST202/205/404); watch-progress 3-way baseline merge + 60s min duration + signature/120s backoff; savedLibrary 500-page pull + 500ms push debounce; remote-wins for watchedItems. Orchestrator: `StartAsync({profileScopedSyncEnabled, runInitialPull})`, 120s cycle, `RequestSyncNowAsync({pushAfterPull})`, `StopAsync()`. Tests: merge matrices, page walk, fallback triggers, debounce with virtual clock. Commit `feat(native): profile sync services`.

### Task 6.3: Scrobble orchestration

**Files:** `Core/Sync/TrackingScrobbleService.cs`, `WatchedSeriesReconciliationService.cs`. Port: fan-out to Trakt (scrobble start/pause/stop, 15s debounce, ≥80% → mark watched, 3-strike cap) + Simkl (watermark-incremental, history writes); series marker vs episodes reconciliation. Tests with fake clients. Commit.

## Phase 7 — i18n

### Task 7.1: Converter + I18n

**Files:** Create: `native/tools/convert-strings.mjs`, `Core/I18n/I18n.cs`, `Core/I18n/KeyAliases.cs` (transcribe the ~230-entry alias map from `js/i18n/index.js`), `Tizen/.../Resources/i18n/*.json` (generated, committed). Test: `I18nTests.cs` + fixture locales.

**Interfaces:**
```csharp
public static class I18n {
    public static Task InitAsync(string preferredLocale, Func<string, Task<string>> jsonLoader); // loader injected: embedded resource in app, file in tests
    public static string T(string key, IReadOnlyDictionary<string,object> args = null, string fallback = null, string locale = null);
    public static string Locale { get; } public static IReadOnlyList<string> SupportedLocales { get; }
    // resolution: override -> system -> en; normalizeLocale: iw->he, in->id, pt->pt-br, zh->zh-cn, strip regions
    // fallback chain: locale -> en -> KEY_ALIASES -> key (+ debug warn); interpolate {{name}}, %1$s/%1$d, %s
}
```

- [ ] **Step 1:** `convert-strings.mjs`: parse each `res/values*/strings.xml` with regex-free XML parsing (`new DOMParser()` not available in Node — use `@xmldom/xmldom`? NO new deps → hand parse with the same regex approach `boot-guard.js` uses, or Node's built-in — use a strict regex over `<string name="...">(.*?)</string>` with escape decode of `\'`, `\"`, `\uXXXX`, `\n`, `&amp;` etc. — same decode set as the JS loader). Output `{locale}.json`. Verify: `node native/tools/convert-strings.mjs && diff` key counts per locale against a count printed from the XML (test asserts en has the same key count as `res/values/strings.xml`).
- [ ] **Step 2:** Failing tests: fallback chain, alias resolution, all three interpolation styles, RTL locale passthrough (ar/he return strings unchanged — no mirroring, same as webapp). Implement; green.
- [ ] **Step 3:** Commit `feat(native): i18n pipeline`.

### Task 7.2: New keys for native-only strings

**Files:** Modify: `res/values/strings.xml` + `res/values-*/strings.xml` (en + the 10 highest-coverage locales: zh-cn, pt-br, pt-pt, it, pl, tr, fr, ru, ta, es-419; others fall back to en). Add keys: `native_p2p_unavailable` ("Direct torrent streaming isn't available in this native build yet. Use a debrid provider or a direct stream."), `native_update_available_body` (update instructions referencing the installer), `native_boot_stage_*` (`shell`, `config`, `storage`, `addons`, `auth`, `profile`) for BootGuard labels. Webapp is unaffected (unused keys). Rerun converter; commit.

## Phase 8 — NUI foundation (device work begins)

### Task 8.1: App skeleton, window, fonts, design tokens

**Files:** Create: `Tizen/Program.cs`, `NuiFoundation/NuvioApp.cs` (extends `NUIApplication`), `NuiFoundation/BootGuard.cs`, `NuiFoundation/DesignTokens.cs`, `Resources/fonts/*.ttf`, `NuiFoundation/SplashView.cs`. Test: device smoke (manual, scripted below).

**Interfaces:**
```csharp
public sealed class NuvioApp : NUIApplication {
    protected override void OnCreate();  // Window 1920x1080, theme init, splash, boot sequence
    protected override void OnPause();   // foreground sync pause (mirrors webOS-style foreground logic in providerCredentialSyncService usage)
    protected override void OnResume();  // ProviderCredentialSyncService.RequestForegroundPullAsync()
    protected override void OnTerminate();
}
public static class DesignTokens { // resolved from css/base.css clamps at 1920px:
    public const int SafeGutter = 56;  public const int SafeGutterWide = 69;   // clamp(28,3vw,56)/clamp(32,3.6vw,72) at 1920
    public const int CardRadius = 21;  public const int CardGap = 18;          // clamp(14,1.1vw,22)/clamp(12,1vw,18)
    public const int ButtonHeight = 60; public const int ControlSize = 64;
    public const int DetailSafeX = 96; public const int EpisodeSafeX = 112;
    public const int TypeCaption = 16, TypeSecondary = 18, TypeBody = 20, TypeSubtitle = 28, TypeTitle = 48;
}
```
- [ ] **Step 1:** Download OFL TTFs (Inter, DM Sans, Open Sans regular/bold/italic variants as used; Material Icons codepoint font) into `Resources/fonts/` with their OFL license files; load via NUI `Window` font extensions or per-`TextLabel` `FontFamily`.
- [ ] **Step 2:** `BootGuard`: full-screen overlay with staged labels (`native_boot_stage_*` via I18n), fatal-error path (`AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` → overlay + dlog), and a `RunCompatibilityGate` that checks `Tizen.System.Information` platform version ≥ 5.5 and exits cleanly otherwise (`Application.Current.Exit`).
- [ ] **Step 3:** Boot sequence in `OnCreate` mirrors `js/app.js` order: `AppConfig.Load` → `JsonFileStore` init → `I18n.InitAsync` → Router init → theme apply → DeviceSessionRegistration.Start → AuthManager.Bootstrap → auth-state routing (Task 18.2 completes the routing; here it shows splash + stage labels only).
- [ ] **Step 4:** Device smoke: `npm run native:deploy`; TV shows splash → stage labels advance → dlog has no unhandled exceptions. Commit.

### Task 8.2: Theme system

**Files:** `Tizen/NuiFoundation/Theme/ThemeColors.cs` (7 palettes WHITE/CRIMSON/OCEAN/VIOLET/EMERALD/AMBER/ROSE × 12 tokens + `-rgb` variants not needed in NUI — expose `Tizen.NUI.Color`), `ThemeManager.cs`, `Core/Settings/ThemeStore.cs` (profile-scoped: theme name, font family, AMOLED flag, language override — keys from Appendix B). `ThemeManager.Apply()` sets static `NuvioTheme.Current` consumed by all widgets (bg `#0d0d0d`, elevated `#1a1a1a`, cardBg, secondary, secondaryVariant, onSecondary, text, textSecondary, textTertiary, border `#333333`, focusColor, focusBg + player-* aliases; AMOLED: bg→`#000000` + optional surfaces). Transcribe palette values verbatim from `js/ui/theme/themeColors.js`. Commit.

### Task 8.3: Image pipeline

**Files:** `Tizen/Platform/ImageCache.cs` (disk LRU: `homeImageCache.v1` semantics — 500 entries / 30d, plus addon logo cache key `nuvio.stream.addonLogoCache.v1` stored as files), `Tizen/Widgets/NuvioImageView.cs`. Behavior: async fetch (shared `HttpClient`) → cache file → `ImageView` via `ImageUrl` local path; placeholder color block + fallback chain (poster→background→logo→colored tile — same order as webapp hero/poster fallbacks). Device smoke: home-less test harness screen showing a grid of 8 remote images with network pull-the-plug test (fallback tiles render). Commit.

## Phase 9 — Navigation + input

### Task 9.1: KeyMap + FocusController

**Files:** `Tizen/Input/NuvioKey.cs`, `KeyMap.cs`, `FocusController.cs`, `HoldTimerService.cs`. Test: `FocusAlgorithmTests.cs` in Core.Tests via a pure `SpatialFocusSolver` class (netstandard2.0, host-testable).

**Interfaces:** `NuvioKey { Up, Down, Left, Right, Ok, Back, Play, Pause, PlayPause, Stop, Ff, Rw, Next, Prev, ChannelUp, ChannelDown, Letter(char) }`; `KeyMap.Normalize(Tizen.NUI.Key key)` → `NuvioKey?` mapping verbatim from `js/platform/sharedKeys.js` + UIScreenMap key table (OK `13/23/Enter`, Back keycodes `8/27/461/10009` + names `Back/Return/GoBack/XF86Back`, media `179/10252/415/19/413/178/417/412/176/177`, letters S/T/C/E/P/B/L; colored/CH keys 403–406/427–429 stay unbound).
`FocusController` ports `js/ui/navigation/screen.js` exactly:
```csharp
public static class SpatialFocusSolver {
    // candidates must move >2px on primary axis; alignment tolerance max(0.7*A, 0.7*B, 48);
    // row/col snap tolerance max(0.9*rect, 42); score = primary*1000 + secondary, aligned first
    public static FocusableRect? FindBest(FocusableRect current, Direction dir, IEnumerable<FocusableRect> candidates);
}
```
`FocusController` keeps `.focused`-equivalent state (applies focus visuals via `IFocusable.ApplyFocus(bool)`), `SetInitialFocus(container)`, `MoveFocus(container, ±1)`, `MoveFocusDirectional(container, dir)`, `HandleDpadNavigation(key)`. `HoldTimerService`: 650ms OK-hold → long-press callback; `SuppressEnterUntilKeyUp` equivalent. Window key events route: `FocusController → modal gate → current screen OnKeyDown/OnKeyUp`; Back: 250ms debounce → `Router.ConsumeRouteReturnBackGuard()` (700ms variant kept) → `screen.ConsumeBackRequest()` → `Router.BackAsync()`.
- [ ] **Step 1:** Host tests for `SpatialFocusSolver`: the 5 tolerance/score cases (below/above/left/right pick, alignment tie-break, row snap) — port expectations from `screen.js` logic. Implement solver; green.
- [ ] **Step 2:** Wire into app; device smoke on a scratch screen of 12 buttons: d-pad navigation matches webapp behavior (incl. wrap boundaries), hold-OK fires long-press once. Commit `feat(native): focus engine`.

### Task 9.2: Router + screens contract

**Files:** `Tizen/Navigation/Route.cs` (enum, 25 routes), `Router.cs`, `RouteParams.cs`, `NavigationContext.cs`, `RouteStateStore.cs` (in-memory Map, `Get/Set/Clear/ClearByPrefix`), `ScreenBase.cs`, `Tizen/Screens/ScreenHost.cs` (single `View` container; mounts/unmounts per route with 8.333vw slide-in transition = 160px translate + 240ms ease).

**Interfaces:**
```csharp
public abstract class ScreenBase : View {
    public abstract Task MountAsync(RouteParams p, NavigationContext ctx);
    public virtual void Cleanup() {}
    public virtual bool? ConsumeBackRequest() { return null; }   // null/false: router handles; "history"; true: consumed
    public virtual bool OnKeyDown(NuvioKey k) { return false; }
    public virtual bool OnKeyUp(NuvioKey k) { return false; }
    public virtual object CaptureRouteState() { return null; }
}
public sealed class Router {
    public Task NavigateAsync(Route r, RouteParams p = null, NavigateOptions o = null);
    public Task BackAsync(BackOptions o = null);
    public Route Current { get; } public ScreenBase CurrentScreen { get; }
    // NON_BACKSTACK: ProfileSelection, AuthQrSignIn, AuthSignIn, SyncCode, ExperienceModeSelection, EssentialAddonSetup
    // Home + Back => PlatformExit(); capture/restore RouteStateStore per route key
}
```
Device smoke: three dummy screens; navigate/back/replace semantics; back on Home exits app; state restored on return. Commit `feat(native): router`.

## Phase 10 — Widget library

### Task 10.1: Cards, rows, grids

**Files:** `Tizen/Widgets/PosterCard.cs` (focused ring `0 0 0 2-4px` focus color + optional scale 1.02–1.1 + focusBg; 16:9 expand-on-focus variant for home), `ContentRow.cs` (horizontal `FlexibleView` + `LinearLayoutManager`, item count `max(15, focusedIndex+1)` render-ahead, ensure-horizontal-visibility scroll 160ms), `PosterGrid.cs` (`FlexibleView` + `GridLayoutManager`, CW virtualization budget: 300 cap / 50 snapshot / 4 load-ahead — port `homeConstants.js` budgets), `WatchedBadge.cs`, `SkeletonLoader.cs` (shimmer keyframes from components.css), `NuvioImageView` fallback chain from 8.3. Device smoke: 200-item grid + rows scroll smoothly on TV (visual check + dlog frame stats if available). Commit.

### Task 10.2: Dialogs, toasts, loading

**Files:** `Tizen/Widgets/NuvioDialog.cs`, `Toast.cs`, `LoadingIndicator.cs` (logo splash + 12-spoke spinner), `OptionMenuController.cs` + `ListPickerDialog.cs` (port `posterOptionsMenu.js` state machine: `createPosterOptionsState/getPosterOptions/activatePosterOption`). Dialog spec verbatim: backdrop + panel + pill buttons, enter fade 200ms + scale 0.92→1 280ms (FastOutSlowIn curve), exit 150ms; `Ok` cycles, `Up/Down` → `OnVerticalNavigate`, modal gate blocks screen keys, 2-frame initial focus delay; widths default 54.2vw ≈ 1040px. Commit.

### Task 10.3: Sidebar + text input

**Files:** `Tizen/Widgets/SidebarNavigation.cs` (legacy rail 144px / expanded 392px + modern pill with 4s auto-collapse; port node list + focus helpers from `sidebarNavigation.js`), `Tizen/Widgets/SearchField.cs` (NUI `TextField` wrapper: focus, on-screen entry via keyboard-less entry — use `TextField` + remote-key typing incl. backspace; placeholder, clear button). Device smoke: sidebar collapse/expand timing, search field accepts letters via remote. Commit.

## Phase 11 — Screens, batch A (low complexity)

Order by dependency: auth first (blocks everything), then onboarding, statics.

### Task 11.1: Auth screens + QR renderer

**Files:** `Tizen/Screens/Account/AuthQrSignInScreen.cs`, `AuthSignInScreen.cs` (email/OTP form + text dialog), `SyncCodeScreen.cs`, `AccountScreen.cs`; `Tizen/Widgets/QrView.cs`.

**Interfaces:** `QrView.Render(string payload, int sizePx)`: `Net.Codecrete.QrCodeGenerator` → boolean matrix → `Tizen.NUI.PixelBuffer` (from Task 0.2 spike; fallback if unavailable: minimal PNG writer — deflate via `System.IO.Compression.DeflateStream` + hand CRC32 — then `ImageUrl` from temp file). AuthQrSignIn port: QR poll loop (`QrLoginService`), countdown, onboarding-gate mode, `skipAuthQrGate` bypass; offline QR fallback image URL (`api.qrserver.com`) not needed natively (local render always available).

- [ ] **Step 1:** Implement QrView + unit-test the CRC32/PNG fallback on host (net8.0 test of the writer).
- [ ] **Step 2:** Port screens (mount flows from `authQrSignInScreen.js` 368 LOC, `authSignInScreen.js` 187, `syncCodeScreen.js` 142, `accountScreen.js` 104 + `accountSettingsContent.js` 192).
- [ ] **Step 3:** Device smoke: fresh sign-out → QR renders → scan with phone → app reaches `Authenticated` → routes onward (profile selection stub until 18.1). Commit `feat(native): auth screens`.

### Task 11.2: Onboarding + static screens

**Files:** `Screens/Onboarding/ExperienceModeSelectionScreen.cs` (115 LOC source), `EssentialAddonSetupScreen.cs` (69), `Screens/SupportersScreen.cs` (943 — static card grid, donors/sponsors via `UniqueContributionsBaseUrl`/`DonationsBaseUrl`), `Screens/LicensesAttributionsScreen.cs` (86 — extend list with native NuGet deps), `Screens/CastDetailScreen.cs` (508 — TMDB person grid), `Screens/DebugConsoleScreen.cs` (327) + `Core/Diagnostics/DebugLogBuffer.cs` (ring buffer port of `consoleDebugBuffer.js`, fed by an app-wide `NuvioLog` facade over dlog). Port each with its FocusController wiring; device smoke per screen. Commit.

### Task 11.3: Addon/plugin screens

**Files:** `Screens/PluginScreen.cs` (445 — addon install: paste-URL field, QR overlay of install URL, repository cards), `PluginsScreen.cs` (62), `CatalogOrderScreen.cs` (278 — reorder grid with `MoveFocus(deltaRow, deltaCol)`). Device smoke: install an addon by URL from the TV → appears in list → reorder works. Commit.

## Phase 12 — Screens, batch B (medium)

### Task 12.1: Search + Discover + See-all

**Files:** `Screens/SearchScreen.cs` (2,058 + `searchCatalogTargets.js`), `Screens/DiscoverScreen.cs` (1,877), `Screens/CatalogSeeAllScreen.cs` (766). Port: search input → catalog `search=` queries per target, rows + see-all, poster holds; discover catalog grid + genre/extra filter pickers; generic dpad fallback paths. Device smoke: search "batman" on cinemeta → grid opens → detail opens (detail is a stub until Phase 14 — show placeholder screen). Commit.

### Task 12.2: Library

**Files:** `Screens/LibraryScreen.cs` (2,260) + `LibraryController.cs` port (1,317 → `Core/Sync/LibraryController.cs`? No — controller is UI state; keep in app as `Screens/Library/LibraryController.cs`): tabs saved/trakt/cloud, filter picker row, grid, list editor dialog, privacy dialog, delete confirm, cloud picker; trakt lists mode. Device smoke across three tabs with test data. Commit.

### Task 12.3: Stream screen (+ P2P-unavailable path)

**Files:** `Screens/StreamScreen.cs` (2,820). Port: zone state machine `{zone: filter|card, row, index, action: play|native}`, chip wrap-around, filter/sort/sort-template settings, badges via `Core/Streams`, autoplay countdown overlay, resume overlay, skeletons. P2P rule: rows whose stream has `InfoHash` and no resolvable debrid path and `Url == null` render with a dimmed state; `Ok` → toast `native_p2p_unavailable`. Debrid-resolvable rows resolve via `DirectDebridResolver` (prepare budget from Core). Device smoke: movie with debrid cached → stream list resolves to direct URL → select → player stub screen shows chosen URL (Phase 15 wires playback). Commit.

### Task 12.4: Folder detail + Trakt screens

**Files:** `Screens/FolderDetailScreen.cs` (1,934 — collections grid; GIF hydration → static images or NUI animated image if trivially supported, else first frame; TMDB enrichment), `Screens/TraktScreen.cs` (828 — stats + rows + dialogs, focusKey memory). Commit.

## Phase 13 — Home screen

### Task 13.1: Home data pipeline

**Files:** `Screens/Home/HomeEngine.cs` (row assembly over `HomeCatalogs` + `homeCatalogPrefs` order/disabled/customTitles; CW budgets: 32 lookups / concurrency 4 / 1s budget / batches 30-18-12; `continueWatchingRenderWindow` port; `nextUpCandidateResolver` port; hero rotation scheduling with static art rotation — trailer playback cut, rotation timings from `homeConstants.js`). Host-test the pure scheduling/windowing parts (CW windowing decisions, rotation schedule). Commit.

### Task 13.2: Home UI engine

**Files:** `Screens/Home/HomeScreen.cs`, `HomeNavModel.cs`, `SpringCamera.cs`. Port `homeScreen.js` (11,097 LOC) minus webOS pointer paths: `navModel {sidebar[], rows[][], tracks[], rowNodesByRowKey}` with row/col zones; left-edge → sidebar; hero L/R rotation; repeat throttle 80ms/112ms; vertical fast-scroll 6400px/s; spring camera (stiffness 180, damping 0.95, 440ms — implement critically-damped spring integrator in `SpringCamera`); track horizontal ensure-visibility 160ms / main vertical 150ms; poster expand on focus (16:9); `L` layout cycle modern→grid→classic (all three layouts render); hold menus via HoldTimerService; `HOME_RETURN_FOCUS_STATE_KEY` restore via RouteStateStore. Device smoke: login → home rows load → d-pad navigation feels parity vs webapp (side-by-side TV check); L cycles layouts; scroll + camera-follow smooth. Commit `feat(native): home screen`.

## Phase 14 — Detail screen

### Task 14.1: Detail hero + episodes

**Files:** `Screens/Detail/MetaDetailsScreen.cs` (hero, action buttons, stream chooser open), `SeasonEpisodeRail.cs` (hold-repeat + `moveEpisodeFocusWithAcceleration`), `DetailNavGraph.cs` — port `handleSeriesDpad` section graph: hero actions → season row → episode rail → insight tabs → ratings/seasons grid → cast rail → morelike rail (remembered indices) → comments → company tracks. Trailer surfaces removed (per decision): hero shows backdrop art rotation only; no trailer buttons/rows. Commit.

### Task 14.2: Detail enrichment surfaces

**Files:** `Screens/Detail/InsightTabs.cs`, `RatingsGrid.cs`, `CastRail.cs`, `MoreLikeRail.cs`, `CommentsSection.cs`, `CompanyTracks.cs`, `StreamChooser.cs` (opens StreamScreen), hold menus (episode/season/poster/hero). TMDB/Trakt/IMDb-ratings/MdbList enrichment via Core clients. Device smoke: series → seasons → episodes → stream chooser → back-state restores focus. Commit.

## Phase 15 — Player core (device-critical)

### Task 15.1: PlayerSession

**Files:** `Tizen/Media/PlayerSession.cs`, `PlayerEvents.cs`, `PlayerOptions.cs`. Test: device smoke (manual protocol below); unit-test pure option mapping on host.

**Interfaces:**
```csharp
public sealed class PlayerSource {
    public Uri Url; public string UserAgent; public IReadOnlyDictionary<string,string> Headers; public string Cookie;
    public IReadOnlyList<ExternalSubtitle> ExternalSubtitles; // uri, name, language
}
public sealed class PlayerSession : IDisposable {
    public Task<PlayerPrepareResult> PrepareAsync(PlayerSource src, View displaySurface, PlayerOptions opts);
    public Task StartAsync(); Task PauseAsync(); Task StopAsync(); Task UnprepareAsync();
    public Task SeekAsync(long positionMs); Task<long> GetPositionAsync(); Task<long> GetDurationAsync();
    public float PlaybackRate { get => SetPlaybackRate; }        // Tizen.Multimedia.Player.SetPlaybackRate
    public IReadOnlyList<PlayerAudioTrack> AudioTracks { get; }  // AudioTrackInfo map + codec labels (audioTrackCodecMetadata port)
    public Task SelectAudioTrackAsync(int index);
    public IReadOnlyList<PlayerSubtitleTrack> SubtitleTracks { get; } // SubtitleTrackInfo map
    public Task SelectSubtitleTrackAsync(int index);
    public Task SetExternalSubtitleAsync(Uri uri, long offsetMs); // SetSubtitle + SetSubtitleOffset
    public event EventHandler<PlayerEvent> Event;                // Buffering, Completed, Interrupted, Error, VideoStreamChanged
    public PlayerDisplaySettings DisplaySettings { set; }        // mode/aspect (4:3/16:9/original/full — port aspect modes), layering under NUI overlay
}
```
Implementation notes: `Tizen.Multimedia.Player` with `MediaUriSource`; `Display` bound to a NUI `View` (player layer below the UI layer — subtitle/overlay views sit above); `Cookie`/`UserAgent` for debrid/hostile-header streams; startup audio gate policy (`startupAudioGatePolicy.js` port: release gate on first buffered frame or fallback option). **Device smoke protocol** (scripted in `tools/tizen-dev.sh smoke-player <url>`): MP4 direct → HLS → DASH → debrid link; seek/rate/tracks via temporary debug keys; measure: startup ms (dlog timestamps prepare→first frame), ABR switch visibility, memory (`sdb shell cat /proc/<pid>/status` RSS) — record in `native/notes/player.md`; abort criteria: startup >2× webapp's or stutter on 1080p HEVC → escalate before building the whole player UI.

### Task 15.2: Track & codec metadata

**Files:** `Tizen/Media/AudioTrackCodecMetadata.cs` (port 200-LOC codec label mapping: TrueHD/DTS/E-AC3-JOC etc.), `TrackMapping.cs` (native index ↔ display index, `mapAudioTrackNativeIndexes` port). Replaces `localMediaTracksRepository` (native Player enumerates embedded tracks). Host tests for label mapping. Commit.

### Task 15.3: Text subtitle renderer

**Files:** `Tizen/Media/Subtitles/SrtParser.cs`, `VttParser.cs`, `AssParser.cs` (styles → cue-level limited rendering), `CueLayout.cs` (port `subtitleCueLayout.js`: ASS `\an1-9` alignment → line/align; VTT line/align), `TextSubtitleRenderer.cs` (NUI `TextLabel` overlay above player layer, safe-area aware, `subtitleVerticalOffset` −20..50 step 5, `subtitlePresentationCapabilities` port: style controls only for text renderer mode). Clock: render loop driven by position polling (250ms) + `SubtitleUpdated` events where the Player supplies SMPTE-TT/WebVTT for HLS/DASH — use native track when selected, renderer for external files. Host tests: parser fixtures + layout math. Device smoke: SRT/ASS offsets + font size cycle. Commit.

### Task 15.4: Bitmap subtitle decoder (PGS/VobSub)

**Files:** `Tizen/Media/Subtitles/PgsDecoder.cs` (segment stream: PCS/WDS/ODS/PCS palette; RLE decode; window composition at 1080p scale), `VobSubDecoder.cs` (idx+sub pairs, MPEG-2 sub-picture RLE), `BitmapSubtitleRenderer.cs` (compositions `{x,y,w,h,rgba}` → `PixelBuffer` → `ImageView` overlay, timed by position). Replaces libbitsub WASM. Host tests: decode golden .sup/.idx+.sub fixtures (generate from libbitsub outputs or hand-crafted segments; assert pixel rects). Device smoke: PGS file alongside playback. Commit `feat(native): subtitle pipeline`.

### Task 15.5: Progress, next-episode, skip-intro

**Files:** `Core/Player/WatchProgressRecorder.cs` (30s/5s timers → `watchProgressItems` cap 5000 + debounced `sync_push_watch_progress`; scrobble ≥80%; `streamResumeIdentity` keys), `Core/Player/NextEpisodeRules.cs` (percentage/minutes-before-end modes, outro segment types), `Core/Player/SkipIntroService.cs` (IntroDB segments + animeSkipSettings). Host tests: thresholds, rules. Commit.

## Phase 16 — Player screen

### Task 16.1: Controls + media keys

**Files:** `Screens/Player/PlayerScreen.cs`, `ControlBar.cs`, `SeekBar.cs`. Port `playerScreen.js` control surface (NOT its engine calls — those go through PlayerSession): control focus zones `skipIntro|progress|buttons`, progress focus + repeat seek preview + 30s FF/RW (keys 417/412), media key matrix (`resolveMediaAction`: 179/10252 toggle, 415/19, 413/178, 176/177), keys S/T/C/E/P/B mapped to subtitle/audio/sources/episode-panel/pause/confirm-startup-error; `shouldReturnToStreamOnBack`, `hasBackDismissableOverlay`. Commit.

### Task 16.2: Dialogs + overlays

**Files:** `Screens/Player/SubtitleDialog.cs` (language rail + style controls when text renderer), `AudioDialog.cs` (tracks + controls columns), `SpeedList.cs`, `SourcesPanel.cs`, `EpisodePanel.cs`, `PauseOverlay.cs` + still-watching 2-choice, `NextEpisodeOverlay.cs` (rules from 15.5), `SeekOverlay.cs`, `StartupErrorOverlay.cs`. Port timing/animation specs from components.css player keyframes. Device smoke (end-to-end): home → detail → stream (debrid) → playback with SRT → subtitle dialog switch → seek → pause → stop → progress reflected in Continue Watching on next launch. Commit `feat(native): player screen`.

## Phase 17 — Settings screen

### Task 17.1: Settings shell

**Files:** `Screens/Settings/SettingsScreen.cs`, `SettingsRail.cs`, `SettingsSectionMeta.cs`, `MarqueeLabel.cs` (90px/s), two-zone nav with `data-focus-key`-equivalent memory (`SETTINGS_UI_STATE_KEY`), rail spring (180/0.95, target 0.42). Sections registry from `SECTION_META` (account, profiles, appearance, layout, plugins, integration, streams, playback, about). Commit.

### Task 17.2: Settings sections + dialogs

**Files:** `Screens/Settings/Sections/*.cs` + option/text dialogs on `NuvioDialog`. Port: appearance (themes with live preview palette swatches, fonts, language w/ 30 locales, AMOLED), layout (home layout previews), integration (debrid: providers, keys via debrid device auth, filters/sort/template DSL editor simplified to pickers; Trakt sign-in/device code; anime-skip; TMDB; MDBList), streams (badge settings, autoplay modes incl. regex), playback (autoplay next episode, rate), about (version, attributions, debug console link, update check). Each section writes through the matching profile-scoped store from Phase 2/6. Device smoke: toggle theme/language/debrid key and verify persistence + reboot. Commit.

## Phase 18 — Profiles + boot completion

### Task 18.1: Profile selection screen

**Files:** `Screens/ProfileSelectionScreen.cs` (port `profileSelectionScreen.js` 2,550 LOC): 6-profile cap, avatars/colors, PIN set/verify dialogs (blink/shake animations), remember-last, primary-profile addon inheritance toggles, add/edit/delete. Commit.

### Task 18.2: Boot orchestration + update prompt

**Files:** `NuiFoundation/BootSequence.cs`, `Tizen/Platform/WebOsResumeRoutes.cs` — NO (webOS-only, dropped); instead: `BootSequence` completes `js/app.js` routing: auth states (`SignedOut` → QR gate with onboarding mode unless bypass; `Authenticated` → `shouldShowProfileSelection` → profile selection or `enterWithLastProfile` → `resolveExperienceRoute` → experienceModeSelection | essentialAddonSetup | home). Foreground lifecycle: `OnResume` → provider credential pull. `Core/Update/AppUpdateService.cs` port (`getLatestAppUpdate` RPC) + `Widgets/AppUpdatePrompt.cs`: shows release notes (HTML-stripped port) + instructions to update via the Nuvio installer (no self-install of tpk). Device smoke: full cold boot to home with real account; kill/resume mid-playback. Commit `feat(native): boot + profiles`.

## Phase 19 — Packaging, deployment, parity

### Task 19.1: Manifest, privileges, signing, deploy scripts

**Files:** `Tizen/tizen-manifest.xml`, `shared/res/icons/*.png` (reuse `assets/icons` set), `native/tools/tizen-dev.sh`, `native/tools/read-version.mjs`.

- [ ] **Step 1:** Manifest: package `NuvioTVN01`, app `NuvioTVN01.NuvioTV`, label "Nuvio TV (Native)", version from `package.json` via `read-version.mjs` (regex-replace in manifest), 1920×1080 splash, `<privileges>`: `http://tizen.org/privilege/internet` + `http://tizen.org/privilege/network.public`? → verify actual WGT privilege strings from `scripts/package-tizen.mjs` config.xml generator and map 1:1; `tv.inputdevice` is WGT-only key registration — NUI receives keys via `Window.KeyEvent` without it (verify on device from Task 0.2 learnings; record in manifest comment).
- [ ] **Step 2:** `tizen-dev.sh` subcommands: `connect|install-run|install <tpk>|run|logs|smoke-player <url>|uninstall` wrapping `sdb`; signing profile `nuvio-native`.
- [ ] **Step 3:** Build + install + launch from clean clone: `npm run native:strings && npm run native:env && npm run native:build && npm run native:deploy`. Commit `feat(native): packaging`.

### Task 19.2: Parity matrix + perf check + docs

**Files:** Create: `native/README.md`, `native/PARITY.md`; Modify: root `README.md` (one paragraph: native Tizen build exists, see native/README).

- [ ] **Step 1:** `PARITY.md`: feature matrix web vs native (25 routes × key flows, player features, sync, integrations) with verified-on-device dates; explicit v1 gaps: P2P streaming, YouTube trailers, webOS (n/a), wrapped-webapp auto-update.
- [ ] **Step 2:** Device perf spot-check vs webapp (the repo has a perf harness for web): hand-timed key-to-visual latency on Home navigation + detail open + player start on both apps on the same TV; record numbers; flag regressions >1.5× webapp for follow-up (startup, home scroll, image grid).
- [ ] **Step 3:** `native/README.md`: toolchain setup (from Task 0.1), build/deploy commands, signing, device notes, architecture map, license (GPL-3.0 + attributions). Update root README + commit `docs(native): readme and parity matrix`.

---

## Appendix A — Key map (port verbatim)

| Keys | Action | Context |
|---|---|---|
| 13 / 23 / Enter | OK/Select | all (dialogs also accept Space 32) |
| 37/38/39/40 | D-pad | FocusController |
| 8 / 27 / 461 / 10009 (+names Back/Return/GoBack/XF86Back) | Back | 250ms debounce → back-guard → consume → Router.Back; Home+Back exits |
| 179 / 10252 | Play/Pause toggle | player |
| 415 / 19 | Play / Pause | player |
| 413 / 178 | Stop | player |
| 417 / 412 | FF / RW ±30s | player |
| 176 / 177 | Next / Prev | player |
| S / T / C / E / P / B | subtitles / audio / sources / episode panel / pause / confirm-error | player |
| L | cycle home layout | home |
| 403–406, 427/428/429 | unbound (parity: keep unbound) | — |

## Appendix B — Persistence keys (localStorage parity; JSON files on device)

`access_token`, `refresh_token`, `is_anonymous_session`, `nuvio_web_installation_id`, `installedAddonUrls`, `installedAddonDisplayNames`, `installedAddonEnabledStates`, `profiles`, `activeProfileId`, `rememberLastProfile`, `hasEverSelectedProfile`, `watchProgressItems` (5000), `watchedItems` (5000), `savedLibraryItems` (1000), `libraryTraktState:{ownerId}` per-profile, `watchProgressSyncState`/`watchedItemsSyncState`, `simklSyncState`, `simklAuthState`, `traktAuthState`, `traktSettings`, `debridSettings`, `streamPreferences` (500), `homeCatalogPrefs`, `collectionsState`, `torrentSettings`, `tmdbSettings`, `mdbListSettings`, `animeSkipSettings`, `continueWatchingPreferences`, `layoutPreferences`, `libraryPreferences`, `trackPreferences`, `themeSettings`, `playerSettings`, `webOsAudioCompatibilitySettings` (carry for future), `streamBadgeSettings`, `homeImageCache.v1` (500/30d), `nuvio.stream.addonLogoCache.v1`, `pluginSources` + `pluginsEnabled`, `traktCachedStats`, `homeContinueWatchingDisplaySnapshot`. Profile-scoped values use the `{__profileScoped:true,version:1,profiles:{pid:value}}` envelope.

## Appendix C — Design constants (resolved at 1920×1080)

Gutters 56/69px (wide), detail safe-X 96px, episode safe-X 112px, hero copy top 40px, card radius 21px, gaps 18px, button 60px, controls 64px, sidebar rail 144px / expanded 392px, type 16/18/20/28/48px, route slide 160px/240ms, dialog 200ms fade + 280ms scale(0.92→1) FastOutSlowIn / 150ms exit, spring camera 180/0.95/440ms, fast-scroll 6400px/s, key-repeat throttle 80/112ms, marquee 90px/s, hold-OK 650ms, back debounce 250ms, Tizen back-guard 700ms, sidebar auto-collapse 4s, CW budgets 32/4/1s/30-18-12.

## Appendix D — Risk register

| Risk | Mitigation |
|---|---|
| TV .NET runtime perf (CoreCLR JIT on 2020 SoC) | Task 0.2 + 15.1 device gates with abort criteria before mass porting; keep allocation churn out of render/focus paths |
| Closed `Tizen.TV.*` widgets unavailable | All widgets hand-built (Phase 10) on open NUI; scoped from librarian findings |
| No emulator on arm64 macOS | Physical TV + scripted smokes via `tizen-dev.sh`; host tests for all logic |
| `PixelBuffer`/font/NUI API gaps on API7 | Spike in Task 0.2; PNG fallback for QR; fonts bundled as files |
| Sync regressions corrupting server state | Golden-fixture payload tests; exact RPC names; remote-wins rules preserved; dual-install (separate package id) isolates webapp users |
| 30-locale i18n drift | Converter in CI-ish npm script; key-count assertion test |
| GPL compliance | Same license; attributions screen lists every new dep |
| Player UX parity (19k LOC source) | PlayerSession API mirrors PlayerController surface used by the screen; end-to-end smoke in 16.2 |

## Appendix E — Coverage matrix (spec → tasks)

Every webapp surface → implementing task: routes: account/auth (11.1), syncCode (11.1), profileSelection (18.1), experienceModeSelection/essentialAddonSetup (11.2), home (13), detail (14), library (12.2), search/discover (12.1), settings (17), debugConsole (11.2), trakt (12.4), supporters/licenses (11.2), plugin/plugins/catalogOrder (11.3), catalogSeeAll (12.1), stream (12.3), castDetail (11.2), folderDetail (12.4), player (16). Core: config (1.2), network (2.2), storage (2.3), models (2.1), auth (3.2–3.3), addons (4), integrations (5), streams (6.1), sync (6.2–6.3), i18n (7), update (18.2), theme (8.2), images (8.3), player engine (15), subtitles (15.3–15.4), progress (15.5), navigation/input (9), widgets (10), boot (8.1/18.2), packaging (19). Deferred by decision: EngineFS P2P (seam in 6.1, notice in 12.3), YouTube trailers (13.1/14.1 surfaces removed), localMedia probing (15.2 native replacement).
