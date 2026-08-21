# Web ↔ Native parity matrix

Legend: ✅ verified · 🟡 implemented, on-device verification pending
(device was offline during the final pass; `sdb connect 192.168.0.152:26101`
then re-run the flows) · ⛔ v1 gap (deferred by decision).

## Routes

| Route | Native screen | Status |
|---|---|---|
| home | HomeScreen (rows, hero rotation, L layout cycle) | 🟡 |
| detail | MetaDetailsScreen (+ DetailEnrichmentView) | 🟡 |
| stream | StreamScreen (badges, P2P-unavailable toast) | 🟡 |
| player | PlayerScreen (media keys, seek, subtitles) | 🟡 |
| search / discover / catalogSeeAll | CatalogScreens base + Search/Discover/SeeAll | 🟡 |
| library | LibraryScreen (saved/trakt/cloud tabs + filters) | 🟡 |
| settings | SettingsScreen rail shell + appearance/about content | 🟡 |
| account | AccountScreen | 🟡 |
| authQrSignIn | AuthQrSignInScreen (QR poll loop, countdown) | 🟡 |
| authSignIn | AuthSignInScreen (remote typing) | 🟡 |
| syncCode | SyncCodeScreen | 🟡 |
| profileSelection | ProfileSelectionScreen | 🟡 |
| experienceModeSelection | ExperienceModeSelectionScreen | 🟡 |
| essentialAddonSetup | EssentialAddonSetupScreen | 🟡 |
| plugin / plugins | PluginScreen / PluginsScreen | 🟡 |
| catalogOrder | CatalogOrderScreen (focus-move reorder) | 🟡 |
| trakt | TraktScreen (device-code flow, stats, watchlist) | 🟡 |
| debugConsole | DebugConsoleScreen over DebugLogBuffer | 🟡 |
| supportersContributors | SupportersScreen | 🟡 |
| licensesAttributions | LicensesAttributionsScreen (native deps added) | 🟡 |
| castDetail | CastDetailScreen (TMDB person + credits) | 🟡 |
| folderDetail | FolderDetailScreen | 🟡 |

## Core subsystems (host-tested)

| Subsystem | Notes | Status |
|---|---|---|
| Config (AppConfig) | 24 keys + env fallbacks | ✅ tests |
| Networking | 401 refresh, concurrency map | ✅ tests |
| Storage | profile-scoped envelopes, caps | ✅ tests |
| Auth + QR login | Supabase RPC parity fixtures | ✅ tests |
| Stremio addons | URL builders, canonicalizer, repos | ✅ tests |
| Integrations | Trakt/Simkl/debrid/TMDB/ratings/introdb | ✅ tests |
| Streams logic | badges, autoplay, template DSL | ✅ tests |
| Sync services | pull/push, merges, debounce | ✅ tests |
| i18n | fallback chain, aliases, interpolation, RTL | ✅ tests |
| Theme | 7 palettes × 12 tokens, AMOLED | ✅ tests |
| Home engine | CW windowing, rotation schedule | ✅ tests |
| Subtitles | SRT/VTT/ASS parsers, CueLayout, PGS RLE | ✅ tests |
| Player rules | next-episode, skip-intro, progress ticks | ✅ tests |
| Focus solver | spatial dpad algorithm port | ✅ tests |

## Player features

- Playback engine: native `Tizen.Multimedia.Player` (HLS/DASH/progressive) 🟡
- Media keys matrix per Appendix A 🟡
- External text subtitles (SRT/VTT/ASS) via TextSubtitleRenderer 🟡
- Embedded track enumeration (selection unavailable in API7 — platform gap) ⛔
- Bitmap subtitles: PGS parser + RLE decode; VobSub idx+sub minimal ⛔ (decoder core only)
- Scrobble fan-out to Trakt/Simkl during playback ✅ (Core, fake-client tested)

## Deferred in v1 (by user decision)

- P2P/EngineFS torrent streaming (seam: `IStreamingServerResolver`)
- YouTube trailer playback (surfaces removed; poster art shown)
- webOS-specific resume routes and wrapped-webapp auto-update install
