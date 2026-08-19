import { readEnvProperties } from "../../scripts/envProperties.mjs";
import { writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const configPath = path.resolve(__dirname, "../src/NuvioTV.Tizen/Resources/config/nuvio.env.json");

async function main() {
  const envProps = await readEnvProperties({ rootDir: path.resolve(__dirname, "../..") });

  const config = {
    SupabaseUrl: envProps.NUVIO_SUPABASE_URL ?? "",
    SupabaseAnonKey: envProps.NUVIO_SUPABASE_ANON_KEY ?? "",
    SupabaseFallbackUrl: envProps.NUVIO_SUPABASE_FALLBACK_URL ?? "",
    TvLoginWebBaseUrl: envProps.TV_LOGIN_WEB_BASE_URL ?? "",
    YoutubeProxyUrl: envProps.YOUTUBE_PROXY_URL ?? "youtube-proxy.html",
    ParentalGuideApiUrl: "https://api.tiffara.com/",
    IntroDbApiUrl: envProps.INTRODB_API_URL ?? "https://api.introdb.app/",
    ImdbRatingsApiBaseUrl: envProps.IMDB_RATINGS_API_BASE_URL ?? "",
    ImdbTapframeApiBaseUrl: envProps.IMDB_TAPFRAME_API_BASE_URL ?? "",
    MdbListApiBaseUrl: envProps.MDBLIST_API_BASE_URL ?? "https://api.mdblist.com/",
    AvatarPublicBaseUrl: envProps.AVATAR_PUBLIC_BASE_URL ?? "",
    UniqueContributionsBaseUrl: envProps.UNIQUE_CONTRIBUTIONS_BASE_URL ?? "",
    DonationsBaseUrl: envProps.DONATIONS_BASE_URL ?? "",
    DonationsDonateUrl: envProps.DONATIONS_DONATE_URL ?? "",
    SponsorNames: envProps.SPONSOR_NAMES ?? "ragmehos.",
    TmdbApiKey: envProps.TMDB_API_KEY ?? "",
    TraktClientId: envProps.TRAKT_CLIENT_ID ?? "",
    TraktClientSecret: envProps.TRAKT_CLIENT_SECRET ?? "",
    TraktApiUrl: "https://api.trakt.tv/",
    TraktRedirectUri: "urn:ietf:wg:oauth:2.0:oob",
    SimklClientId: envProps.SIMKL_CLIENT_ID ?? "",
    SimklApiUrl: "https://api.simkl.com",
    SimklAppName: envProps.SIMKL_APP_NAME ?? "nuvio",
    PremiumizeClientId: envProps.PREMIUMIZE_CLIENT_ID ?? ""
  };

  await writeFile(configPath, JSON.stringify(config, null, 2));
  console.log(`Generated ${configPath}`);
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
