import { readEnvProperties } from "../../scripts/envProperties.mjs";
import { writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const configPath = path.resolve(__dirname, "../src/NuvioTV.Tizen/Resources/config/nuvio.env.json");

async function main() {
  const { env: envProps } = await readEnvProperties({ rootDir: path.resolve(__dirname, "../..") });

  const config = {
    supabaseUrl: envProps.NUVIO_SUPABASE_URL ?? "",
    supabaseAnonKey: envProps.NUVIO_SUPABASE_ANON_KEY ?? "",
    supabaseFallbackUrl: envProps.NUVIO_SUPABASE_FALLBACK_URL ?? "",
    tvLoginWebBaseUrl: envProps.TV_LOGIN_WEB_BASE_URL ?? "",
    youtubeProxyUrl: envProps.YOUTUBE_PROXY_URL ?? "youtube-proxy.html",
    parentalGuideApiUrl: "https://api.tiffara.com/",
    introDbApiUrl: envProps.INTRODB_API_URL ?? "https://api.introdb.app/",
    imdbRatingsApiBaseUrl: envProps.IMDB_RATINGS_API_BASE_URL ?? "",
    imdbTapframeApiBaseUrl: envProps.IMDB_TAPFRAME_API_BASE_URL ?? "",
    mdbListApiBaseUrl: envProps.MDBLIST_API_BASE_URL ?? "https://api.mdblist.com/",
    avatarPublicBaseUrl: envProps.AVATAR_PUBLIC_BASE_URL ?? "",
    uniqueContributionsBaseUrl: envProps.UNIQUE_CONTRIBUTIONS_BASE_URL ?? "",
    donationsBaseUrl: envProps.DONATIONS_BASE_URL ?? "",
    donationsDonateUrl: envProps.DONATIONS_DONATE_URL ?? "",
    sponsorNames: envProps.SPONSOR_NAMES ?? "ragmehos.",
    tmdbApiKey: envProps.TMDB_API_KEY ?? "",
    traktClientId: envProps.TRAKT_CLIENT_ID ?? "",
    traktClientSecret: envProps.TRAKT_CLIENT_SECRET ?? "",
    traktApiUrl: "https://api.trakt.tv/",
    traktRedirectUri: "urn:ietf:wg:oauth:2.0:oob",
    simklClientId: envProps.SIMKL_CLIENT_ID ?? "",
    simklApiUrl: "https://api.simkl.com",
    simklAppName: envProps.SIMKL_APP_NAME ?? "nuvio",
    premiumizeClientId: envProps.PREMIUMIZE_CLIENT_ID ?? ""
  };

  await writeFile(configPath, JSON.stringify(config, null, 2));
  console.log(`Generated ${configPath}`);
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
