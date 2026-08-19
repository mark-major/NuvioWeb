# Target device (2026-08-19)

- Model: QE55Q80TATXXH (Samsung Q80T 2020, 55") — Tizen 5.5 platform
- Connection: `sdb connect 192.168.0.152:26101` — works; `sdb devices` lists it in `device` state
- `sdb capability`: secure_protocol:enabled, sdbd_rootperm:disabled, rootonoff:disabled,
  filesync_support:pushpull, intershell_support:disabled
- Restrictions observed: `sdb shell` returns no output (channel blocked in this dev-mode state);
  `sdb dlog` empty; `sdb push` to `/tmp` rejected ("cannot push files to this path") — push into
  app-shared paths or use `sdb install` / `tizen run` which drive the install API directly.
- Verification strategy on this device: `sdb install`/`tizen run` exit codes + visual confirmation
  by the developer watching the TV. dlog-based evidence is unavailable until/unless shell unlocks.
- TV IP: 192.168.0.152 (Developer Mode enabled; port 26101 open)
