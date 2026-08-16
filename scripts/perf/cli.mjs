const USAGE = `Usage: npm run perf -- <command> [options]

Commands:
  login                     One-time interactive sign-in; saves session for runs
  run [--steps <list>]      Run the measurement protocol (default: all steps)
      [--reps <n>]          Repetitions per step (default 5, +1 warmup)
      [--cpu <n>]           CPU throttle factor (default 4; 1 = off)
      [--net tv|off]        Network profile (default off)
      [--no-tv]             Plain desktop profile (no Tizen UA / 1080p)
      [--headed]            Show the browser
      [--url <url>]         App URL (default http://127.0.0.1:4173)
      [--skip-build]        Don't build before ensuring the server
      [--keep-traces]       Keep raw .json.gz traces (kept by default anyway)
      [--out <dir>]         Output dir (default perf-runs/<timestamp>)
  compare <runA> <runB>     Diff two runs; writes compare-<A>-<B>.html
Step ids: home_dpad_row home_dpad_rows grid_seeall grid_library
          transition_detail transition_settings settings_theme_toggle
Special: --steps smoke (1 rep of home_dpad_row)`;

export function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith("--")) {
        flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(a);
    }
  }
  return { command: positional[0] || null, positional, flags };
}

async function main() {
  const { command } = parseArgs(process.argv.slice(2));
  if (!command || command === "help" || command === "--help") {
    console.log(USAGE);
    return 0;
  }
  if (command === "login") {
    const { runLogin } = await import("./login.mjs");
    return runLogin(parseArgs(process.argv.slice(2)).flags);
  }
  if (command === "run") {
    const { runProtocol: exec } = await import("./runner.mjs");
    return exec(parseArgs(process.argv.slice(2)).flags);
  }
  if (command === "compare") {
    const { writeCompare } = await import("./compare.mjs");
    const { positional } = parseArgs(process.argv.slice(2));
    if (positional.length < 3) {
      console.error("compare requires two run dirs");
      return 2;
    }
    return writeCompare(positional[1], positional[2]);
  }
  console.error(`Unknown command: ${command}\n`);
  console.log(USAGE);
  return 2;
}

main()
  .then((code) => process.exit(code ?? 0))
  .catch((err) => {
    console.error(err);
    process.exit(1);
  });
