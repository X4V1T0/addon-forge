# Changelog

## 0.1.47

- Fix: an app whose own entrypoint creates directories as root inside a persisted/shared path before
  dropping privileges (example-app: `mkdir -p $DATA_DIR/plugins`, then `setpriv` to
  uid 1000) failed with `ln: ... Permission denied` - its `chown -R` doesn't follow addon-forge's
  symlink, so those new directories stayed root's. The target's directories are now setgid and the app
  starts under `umask 002`, so anything created there inherits the original group, group-writable.
  Verified in Docker with an entrypoint reproducing that sequence.

## 0.1.46

- Fix: a persisted or shared path now keeps the owner of the image's own directory. Files copied into
  `/share/<slug>/...` over Samba belong to root, and an app that starts as root, `chown -R`s its data
  directory and then drops privileges (e.g. example-app, uid 1000) never fixed them:
  `chown -R` doesn't follow the symlink it now finds there. It then couldn't write its own data.
- Fix: `${VAR}` / `${VAR:-default}` references are resolved when reading option defaults, instead of being
  kept as literal text. `ENV APP_SERIES=${APP_SERIES}` (with `ARG APP_SERIES=5.13`) produced a
  `${APP_SERIES}` default that was exported over the image's own value, and compose's
  `VNC_PASSWORD: ${VNC_PASSWORD:-}` produced a non-empty `${VNC_PASSWORD:-}` password. A compose volume like
  `${DATA_DIR:-./data}/config:/config` is no longer split on its own `:-` either, so its container path
  is detected correctly.

## 0.1.45

- Fix: a repo whose `.dockerignore` is allowlist-style (`*` followed by `!src/`, `!docker/`...) - e.g.
  example-app - failed to build in Home Assistant with `"/addon-forge-entrypoint.sh":
  not found`, because that ignore file also dropped addon-forge's own generated files from the build
  context. The repo's `.dockerignore` (and `Dockerfile.dockerignore`, if present) now gets a trailing
  `!addon-forge-*` line re-including them.

## 0.1.44

- Fix: an environment variable whose name merely *contains* "PASS" - e.g. `AGENT_BYPASS_DB_VOLUME_CHECK` -
  was classified as a password. With its `true` default, the generated `config.yaml` then had a
  `password` option holding `true`, and Home Assistant refused to save the addon's configuration
  ("expected str. Got {...}"). "PASS" now only counts as a whole word or inside PASSWORD/PASSWD/PASSPHRASE.
- Fix: a string default that looks like a bool or number (`'true'`, `'8080'`) is now written quoted in
  `config.yaml`, so it can never be re-read as the wrong type.

## 0.1.43

- Fix: sharing or persisting a directory that a **published image itself** declares as a `VOLUME`
  (e.g. `/usr/share/geodata` in example/geodata) could never work, and only said so with one line
  in the addon's log ("could not persist ... a mount point?"). A `VOLUME` is inherited from the image
  and a derived Dockerfile can't remove it - confirmed directly: still listed under `Config.Volumes`
  after dropping the `VOLUME` line, and `rm -rf` on it fails with "Resource busy" - so Docker mounts a
  volume there and a mount point can't become a symlink. Generation now fails up front with an
  explanation instead, and the Add addon page no longer pre-fills an image's volumes as persistent
  paths (it explains the limitation instead). Point the app at a different directory - e.g.
  `GEODATA_DIR=/geodata` - and share that. Verified end-to-end against the real image: the
  symlink is created, geodata writes through it into `/share`. A git-sourced addon's own `VOLUME`
  lines are still stripped as before.

## 0.1.42

- Fix: a persistent or shared path under `/usr` was silently dropped - `/usr` was forbidden as a
  whole subtree, so a legitimate data directory inside it (geodata's own
  `VOLUME /usr/share/geodata`, nginx's `/usr/share/nginx/html`...) never made it into the addon: no
  symlink, no `share:rw`, and the `VOLUME` left in place. Now only what a container genuinely needs
  to run is off limits (`/usr/bin`, `/usr/sbin`, `/usr/lib*`, `/usr/libexec`, `/usr/include`,
  `/usr/local/bin`, `/usr/local/sbin`, and `/usr`, `/usr/local`, `/usr/share` themselves).
- Fix: the "N entries were ignored" note after saving persistent/shared paths never said *which*
  entries, which is how the above went unnoticed. It now names them ("Rejected: '/usr/share/geodata'").

## 0.1.41

- Fix: a config file template (`ConfigFileTemplate`/`ConfigFileDestPath`) could never actually be
  delivered to a non-root final image at all - only degrade gracefully (0.1.40) instead of crashing.
  jq's setuid bit only helps *read* `/data/options.json`; the destination write is done by the
  calling shell's own fd, which a non-root user simply can't open in a directory it doesn't own.
  Fixed properly this time: the destination file (and, if needed, its directory) is now pre-created
  at build time with a world-writable mode via `COPY --chmod` - the same build-time-only mechanism
  already used to make jq itself setuid-root, and unaffected by whatever `USER` is active. Confirmed
  directly end-to-end against a real build (example-app's own
  `gcr.io/distroless/static-debian13:debug-nonroot`, uid 65532): its `config.yaml` now gets written
  with real rendered values, no crash, no fallback needed. Also fixes the fallback's own copy: it
  used `cp`, which - confirmed directly - unlinks and recreates its destination rather than
  truncating it in place, needing directory write permission the pre-seeded file deliberately
  doesn't grant; it's `cat template > dest` now, which only needs the file's own permission (already
  granted). A dead end tried and rejected along the way, for the record: a second setuid helper
  binary to perform the write itself, the same way jq performs the read - busybox's own `cp`/`mkdir`
  applets deliberately drop their setuid privilege before doing any file I/O, confirmed directly
  (`busybox id`, invoked setuid-root, reports the caller's real uid, not 0).

## 0.1.40

- Fix: an addon with both a config file template and a non-root final image (e.g. example-app's
  distroless `nonroot`) could crash-loop instead of starting. Writing the rendered config file isn't
  covered by the setuid-`jq` trick used to read `/data/options.json` - that only elevates *reading*
  that one file, not creating an arbitrary destination file as this addon's own (non-root) user. When
  the destination truly isn't writable, the render-or-copy fallback used to leave `set -e` to abort
  the whole entrypoint before the app ever started. Confirmed directly against the real image
  (`gcr.io/distroless/static-debian13:debug-nonroot` as uid 65532): a third fallback now logs a
  warning and lets the app start anyway - matching how example-app (and most apps with an optional
  config file) already handles a missing config file gracefully on their own.

## 0.1.39

- Fix: a Docker-image-sourced addon (0.1.38) with no reliable web URL for its source (e.g.
  `example/proxy` - not itself a URL) wrote that bare reference straight to config.yaml's `url:`.
  The Supervisor's own schema validates that field strictly as a URL, and refused to even load the
  generated addon ("expected a URL for dictionary value"). `url:` is now only ever a real,
  constructed link (Docker Hub/ghcr.io, from the image reference) or omitted entirely - never the
  bare reference itself.
- Fix: a Docker-image-sourced addon's multi-arch manifest list had Docker's own platform names
  (`arm64`, `386`...) written straight to config.yaml's `arch:` list. The Supervisor's schema only
  accepts its own five names (`aarch64`, `amd64`, `armhf`, `armv7`, `i386`) and rejects anything
  else outright, refusing to load the addon. Each platform is now translated to Home Assistant's own
  naming (or dropped, for an architecture with no HA equivalent at all - ppc64le, s390x, riscv64...)
  before it's ever written.

## 0.1.38

- New: shared paths. A container path can now be symlinked into `/share/<slug>/...` instead of
  `/data`, so a *different* addon can read it via its own `share:ro` map entry (e.g. a bouncer addon
  reading a reverse proxy's access log, which it otherwise has no way to see inside that addon's
  private `/data`). Adds `share:rw` to this addon's own `map:` automatically. A path can't be both
  persistent and shared.
- New: addons can now be added from an already-published Docker image (e.g.
  `docker.io/example/agent:v1.6.3` or `ghcr.io/example/proxy`) instead of only a git repo
  with a Dockerfile — no clone, no build context. addon-forge inspects the image's manifest directly
  against its registry (Docker Hub, ghcr.io, or any standard OCI registry) for its
  ports/volumes/env/entrypoint. If nothing needs delivering beyond what the image already does (no
  options, no config file, no persistent/shared data), the Supervisor pulls the image directly with
  no build at all; otherwise a minimal Dockerfile is built around it, same as a git-sourced addon.
  "Watch for updates" picks the latest semver-ish tag from the image's own registry.
- New: `host_network` and manually-declared ports. Some images (typically ones built around Docker's
  own `network_mode: host`) declare no ports at all, so addon-forge has nothing to auto-detect —
  ports can now be added by hand, one per line, and merged with whatever WAS detected. Confirmed
  directly against the Supervisor's own source that once `host_network` is on, the port mapping
  becomes informational only (Docker does no port publishing under host networking) — said so in the
  generated DOCS.md too.

## 0.1.37

- Fix: a user-supplied media library (`/music`, `/photos`, `/media`, `/downloads`...) was flagged as
  "data at risk" — e.g. Navidrome's `VOLUME ["/data", "/music"]`. That folder is content you point
  the app at (an addon option or a mapped folder), not state the app creates, so nothing is lost on
  an update. Such paths are now listed as a weak signal only (no warning, not pre-selected), and a
  path that is also an option's default value now says so ("also the default of ND_MUSICFOLDER").
- New: "Not needed, dismiss" on the data-at-risk warning, for a false positive that shouldn't be
  persisted; it changes nothing about the addon. (Saving the Persistent data list with nothing in
  it does the same.) Addons already scanned keep their previous result until dismissed or until
  their next real update, since the scan only re-runs then.

## 0.1.36

- New: addon-forge now detects, when analyzing a repo, container paths an app writes state to that
  Home Assistant would silently lose on every rebuild (it only preserves `/data` per addon). Found
  the hard way: an addon (subwave) came back asking to be configured from scratch after a routine
  update, because its supervisor script hardcodes `STATE_DIR=/var/sub-wave` right before launching
  the app — so no env var can move it. Signals: `VOLUME` declarations, compose volumes, `ENV
  *_DIR`/`*_PATH` variables named like data/state/storage/db (weaker: config/upload/library/media),
  and directories created writable at build time (`mkdir` + `chmod`/`chown`). Anything that is, or
  contains, the app's own code (`WORKDIR`, a `COPY` destination) is never guessed.
- New: a "Persistent data" setting (Add addon page and addon detail page). Each listed path becomes a
  symlink into `/data/persist/...` when the addon starts, so the app keeps writing exactly where it
  always did and the data survives rebuilds. The first start copies the image's own directory over
  (defaults, permissions); afterwards `/data` always wins, so an update never overwrites real data.
  Any `VOLUME` on a persisted path is removed from the generated Dockerfile (a mount point can't be
  replaced by a symlink). Requires the image to start as root; otherwise it is skipped with a message
  in the addon's log. Verified against real containers on Debian and Alpine: state and the original
  seed file survived rebuilding the image with different defaults; directory mode was preserved;
  a non-root run warns and still starts the app.
- New: a "Data at risk" warning on the dashboard card and detail page for any addon with strongly
  detected state paths that nobody has reviewed yet, with a one-click "Persist these & regenerate".
  Addons created before this version are scanned once on their next check (no generated files are
  touched) so existing ones get the warning too.
- Fix: for a repo built from a non-root Dockerfile (picked from the list of candidates), analysis
  only ever read the root Dockerfile — which doesn't exist there — so that Dockerfile's `VOLUME`,
  `ENV` and `EXPOSE` were never seen, and compose volumes were taken from whichever service
  happened to be listed first (subwave's DOCS.md listed a reverse proxy's `/data` and `/config`).
  Analysis now reads the chosen Dockerfile, and compose volumes only come from the service whose
  `build:` actually builds it. Side effect: such an addon's next regeneration may add new options
  for the Dockerfile's `ENV` vars, the same as any root-Dockerfile addon already gets.

## 0.1.35

- Fix: 0.1.32-0.1.34 all took the same approach — become root to read `/data/options.json`, then drop
  back to the app's own declared user before handing off — and it turns out that approach is broken
  at the root (pun intended), not just in the specific tool used. Confirmed directly, the hard way,
  across `su`, `setpriv`, `chpst` and `gosu` alike: dropping privileges via any of them leaves the
  dropped process *non-dumpable*, a Linux kernel security measure triggered by the credential
  transition itself. A non-dumpable process's `/proc/PID/fd/*` can't be opened by anyone else,
  including its own same-UID children, without `CAP_SYS_PTRACE` — which no non-root container
  process has. This broke a real addon whose cron setup (tini forking a script that writes to its
  own parent's `/proc/1/fd/1`, a common trick to make cron output show up in `docker logs`) failed
  with "Permission denied" no matter which drop tool 0.1.32-34 used.
- The actual fix: never change this wrapper's privilege level at all. `jq` itself (still fetched from
  its own official image via `COPY --from`) is now made setuid-root instead, and used to read
  `/data/options.json` and render a config file template — the *file* needs root to read, not the
  process asking it to. The wrapper script, and the app it hands off to, now run as whatever user the
  addon's own Dockerfile declared from the very start, exactly like a native (non-addon-forge)
  container would — no `USER root`, no privilege drop, no RUN instruction at all anymore, on any base
  image. This also means jq no longer needs a package-manager install or python fallback (removed),
  and config file rendering no longer needs `envsubst`/`gettext` (removed) — both now always use the
  same setuid jq. Verified end-to-end against a real container, using the actual real-world addon
  that surfaced this: no more "Permission denied", and its `/data/options.json` values still reach
  the app correctly.

## 0.1.34

- Fix: 0.1.32/0.1.33's `su`-based privilege drop introduced a subtler problem on Debian-based
  addons - a cron job writing to `/proc/1/fd/1` (a common trick to make cron output show up in
  `docker logs`) failed with `can't create /proc/1/fd/1: Permission denied`. Confirmed directly:
  Debian's `su -c` doesn't exec-replace itself like the docs imply it should for this use case - it
  forks a child to run the command and stays behind *as PID 1, running as root* itself, so the
  non-root child could never touch root-owned PID 1's file descriptors. `setpriv --reuid/--regid`
  (util-linux) does exec-replace correctly instead - confirmed directly on real Alpine and Debian
  containers, for both a named `USER` and a bare numeric one - so it's now tried first. Alpine's own
  `setpriv` is a stripped busybox build with no `--reuid`/`--regid` at all though (also confirmed
  directly), so this probes for real support before relying on it, still falling back to `su` (which
  *does* exec-replace correctly on Alpine specifically) otherwise - same synthetic-passwd-entry
  handling as 0.1.33 for a numeric UID there. Verified end-to-end on real Alpine and Debian
  containers, named and numeric `USER` alike, that the actual app ends up as PID 1 in every case.

## 0.1.33

- Fix: an addon whose Dockerfile declares a raw numeric `USER 1000:1000` (or bare `USER 1000`) -
  common for images that never bother creating a named account - failed the 0.1.32 fix with
  `su: unknown user 1000:1000`. Confirmed directly: `su` only ever resolves accounts *by name*, never
  a bare numeric UID/GID, even when a real matching `/etc/passwd` entry exists for it. A numeric
  `USER` now gets a one-off synthetic `/etc/passwd` (and `/etc/shadow` - Debian's own `su` otherwise
  refuses with "Authentication failure", also confirmed directly) entry mapping it to a name of
  addon-forge's own choosing, sidestepping the need for `adduser`/`useradd` (whose own availability
  varies just as much across distros as the drop tool itself did). A named `USER` (e.g. `nonroot`)
  is unaffected, still resolved directly. Distroless's `chpst -u` already handled bare numeric IDs
  correctly on its own and needed no change. Verified end-to-end against a real container that the
  app ends up running with the exact original uid/gid, not root.

## 0.1.32

- Fix: any addon whose own Dockerfile sets a non-root `USER` before addon-forge's entrypoint wrapper
  gets appended (not just distroless ones) failed to start at all —
  `[FATAL tini (7)] exec /addon-forge-entrypoint.sh failed: Permission denied`. The `RUN chmod +x`
  that makes the wrapper script executable ran as that non-root user, but the script was just
  `COPY`'d in as root (COPY's default ownership doesn't follow whatever `USER` is active) — a
  non-owning, non-root user can't `chmod` a file it doesn't own, so it silently stayed
  non-executable and the container could never start. Reproduced directly against a real container
  (had to work around Windows/Docker Desktop's own file-permission quirks to do it reliably) before
  fixing it. addon-forge's own setup step now runs as `root` here too (like the distroless case
  already did), then drops back to running the actual app as whatever user its Dockerfile originally
  declared — via plain `su`, confirmed present on both Alpine and Debian (and derivatives), unlike
  su-exec/gosu which aren't guaranteed. Confirmed end-to-end that the app then runs as its original
  user again, not root.

## 0.1.31

- Fix: "Save & regenerate" on an addon's Config file or Extra environment variables card failed with
  `The instance of entity type 'ManagedAddon' cannot be tracked because another instance with the
  same key value for {'Slug'} is already being tracked` — the page handler saved the addon, then
  called into `AddonUpdateService.RegenerateAsync`, which re-fetches and saves the same addon again
  itself; since a request's DbContext is shared across both calls, the first save left its entity
  attached and the second (a different in-memory instance, same row) collided with it. `UpdateAsync`
  now detaches its entity once saved, so it doesn't linger and conflict with a later fetch in the
  same request. Reproduced first with a real EF Core/SQLite test to confirm the exact error message
  before fixing it.

## 0.1.30

- Fix: on a `nonroot`-tagged distroless addon (like example-app with "Allow a distroless debug
  shell" on), the wrapper failed with `jq: error: Could not open file /data/options.json: Permission
  denied` and `mkdir: can't create directory '/app': Permission denied` — running as that image's
  own non-root user, it could read neither the Supervisor-owned options file nor create a
  not-yet-existing config file destination. Reproduced directly against a real container before
  fixing it. The wrapper's setup step now runs as `root` (needs to, to read/write those), then drops
  back to running the actual app as whatever user its own Dockerfile originally declared via `USER`
  (confirmed directly it does) — so the app itself keeps its original hardening; only addon-forge's
  brief setup step needed the extra privilege. An app whose Dockerfile never declares a `USER`
  explicitly (relying on a non-root default baked into the base image itself, which isn't visible
  from Dockerfile text) still ends up running as root, for lack of anything to drop back to.

## 0.1.29

- Fix: a `${OPTION_NAME}` used in the config file template never showed up on the addon's own
  Configuration tab unless that same name was *also* separately detected or added under "Extra
  environment variables" — the template only ever consumed existing options, it never created one
  from being referenced. Any placeholder not already known is now automatically added as its own
  option too, so writing `${WEATHER_LOCATION}` in the template is enough on its own; an existing
  detected/manual option of the same name is left untouched (the template only ever fills a gap,
  never overrides one).

## 0.1.28

- Fix: "Allow a distroless debug shell" (0.1.27) still failed to build — distroless's "debug" tag
  ships busybox at `/busybox/sh`, not `/bin/sh`, but Docker's `RUN <command>` always invokes
  `/bin/sh` literally (never resolved via PATH), so the appended install step failed with
  `exec: "/bin/sh": no such file or directory` even after switching tags. Worse, distroless has no
  package manager at all, debug tag or not, so that install step (`jq`, and `gettext` for
  `envsubst`) could never have worked there anyway. Both are now fixed at the root: on a distroless
  target, the wrapper no longer runs any `RUN` command — `jq` is fetched from its own official,
  statically-linked image (`COPY --from=ghcr.io/jqlang/jq`, needs no shell at all to run) and the
  entrypoint script itself execs via `/busybox/sh`; the config file template is now rendered with
  `jq` directly (verified not to re-evaluate anything inside substituted values) instead of
  `envsubst`, dropping that dependency entirely on this path. Verified against a real `docker build`
  + `docker run` of a distroless-nonroot addon, not just reasoned about — also surfaced and
  documented a related, separate gotcha: a `nonroot` image can't write to most of the filesystem, so
  the config file's destination path needs to actually be writable (e.g. under `/tmp`).

## 0.1.27

- New: "Allow a distroless debug shell" — an explicit, opt-in checkbox (Add addon form, and an
  addon's detail page next to its config file) that switches a distroless final build stage to
  distroless's own "debug" tag variant (e.g. `nonroot` → `debug-nonroot`), which ships a minimal
  busybox shell for exactly this purpose. Off by default: it removes some of the hardening (no
  shell, no package manager, smaller attack surface) the upstream image deliberately shipped with,
  so addon-forge never does this on its own — only when explicitly asked to, per addon. Noted in the
  generated addon's own DOCS.md when applied. No-op if the final stage isn't distroless (or is
  already on a debug tag, or is pinned by digest, where guessing an equivalent debug digest isn't
  safe).

## 0.1.26

- Fix: a final build stage on a distroless base image (e.g. example-app's
  `gcr.io/distroless/static-debian13:nonroot`) broke the Supervisor's build outright when the addon
  had any options or a config file template — addon-forge only recognized the literal `FROM scratch`
  as having no shell, so it still appended an entrypoint-wrapping `RUN`/`ENTRYPOINT` that needs
  `/bin/sh`, which doesn't exist on distroless either, and the build failed on that `RUN` instruction.
  Distroless images (`gcr.io/distroless/*`) are now detected the same way `scratch` is: addon-forge
  skips wrapping and says so in DOCS.md instead of generating an addon that can't build. This means
  an addon with a distroless final stage still can't receive options as env vars or a rendered config
  file automatically — that limitation was already true for `scratch`, distroless just wasn't being
  caught the same way.
- Fix: a `FROM --platform=$BUILDPLATFORM some/image` line (common in multi-stage cross-build
  Dockerfiles) could be misread as the image being literally named `--platform=$BUILDPLATFORM`,
  since the check only looked at the first token after `FROM`. It now skips any leading `--flag`
  tokens before reading the actual image reference.

## 0.1.25

- New: a config file can now be templated by hand, for settings an app only reads from a file, not
  environment variables (e.g. a dashboard app's weather config). Paste the file's content and replace
  whatever should be configurable with `${OPTION_NAME}` placeholders referencing an option above
  (detected or manually added), and give the destination path inside the container. Reuses the same
  entrypoint-wrapper mechanism that already delivers options as env vars: the wrapper now also
  renders the template (via `envsubst`, installed at build time the same best-effort way as `jq`)
  and writes it to that path before the app starts. Format-agnostic — it's plain text substitution,
  never actually parsed, so it works for YAML, JSON, TOML, INI or anything else. Available on the
  "Add addon" form and, since this is often only discovered after the addon already exists, also on
  an addon's detail page — saving there regenerates and rewrites it immediately. Kept across every
  future regeneration the same way folder mappings, extra env vars and a manual icon already are.

## 0.1.24

- Fix: on the Dashboard, a branch-tracked addon's status badge ("Up to date" etc.) dropped below
  its card instead of staying top-right on narrow screens — its full commit SHA has no natural break
  point, so the browser grew the name/meta column to fit it and left no room for the badge on the
  same line, wrapping it underneath. The meta column now wraps its own long text (including the
  SHA) within the space available instead of pushing outward, and the badge is pinned to the corner
  and no longer allowed to wrap at all.

## 0.1.23

- New: an icon (PNG/JPEG) can now be uploaded by hand, for repos analysis doesn't find one in (an
  SVG-only favicon, a logo kept somewhere addon-forge doesn't look, or no logo at all). Available on
  the "Add addon" form after analyzing, and — since a missing icon is often only noticed after the
  addon already exists — also on an addon's detail page, with a live preview of whatever icon is
  currently in effect. A manual upload always overrides auto-detection and, like folder mappings and
  extra env vars, is kept across every future regeneration instead of getting silently dropped.
  Uploading on an existing addon's detail page regenerates and rewrites it immediately.

## 0.1.22

- New: extra environment variables can now be added by hand, as many as needed — for options a repo
  only documents in its README/wiki instead of its Dockerfile, compose file or .env.example (which
  addon-forge has no way to read on its own). Available on the "Add addon" form (a `KEY=value`
  textarea next to the "Detected" preview, same convention as a .env.example) and, since these are
  often only discovered after a first failed start, also editable afterwards on an addon's detail
  page — saving there regenerates and rewrites the addon immediately, on its current version,
  instead of waiting for the next real upstream update. Kept across every future regeneration the
  same way manual folder mappings already are.

## 0.1.21

- UX: full visual pass on addon-forge's own UI. New color theme matching the forge icon (dark
  navbar, warm accent color instead of Bootstrap's default blue). Fixed a real mobile bug: the
  navbar had no collapse toggler and used a breakpoint class that hid the entire menu on phones —
  it's now a proper responsive navbar with a hamburger button. The Dashboard's addon list is no
  longer a `<table>` with three buttons crammed into one cell (the worst-offending mobile layout
  in the app) — it's now a card list per addon, with "Check now" as the one visible action and
  "Stop managing"/"Delete from store" moved into a dropdown menu. Minor polish elsewhere: the addon
  detail page's header wraps instead of overflowing on narrow screens, and the detected-options
  table on Add addon scrolls horizontally instead of squeezing off-screen.

## 0.1.20

- Cosmetic: replaced the hand-drawn anvil icon with a proper pixel-art forge scene (user-provided).

## 0.1.19

- Cosmetic: replaced the placeholder "AF" icon/logo with a retro anvil-and-flame badge.

## 0.1.18

- New: a "Delete from store" action (Dashboard and the addon's detail page) actually removes
  `/addons/<slug>` from disk, instead of only "Stop managing" (which only stopped addon-forge's own
  tracking and left the files behind, requiring manual cleanup on the host). Requires an explicit
  confirmation that warns about uninstalling in Home Assistant first — irreversible.
- New: every action (Analyze, Confirm, Check now, Publish...) now disables the clicked button and
  shows a spinner immediately on submit, instead of the page looking frozen for the few seconds a
  git clone or LLM call can take.

## 0.1.17

- New: a "Folder mappings" field on the Add addon form lets you manually add Home Assistant `map:`
  entries (e.g. `media:rw` for the shared media folder) — addon-forge has no reliable way to guess
  which host folder a container path like `/music` is supposed to be, so this is always a manual,
  explicit choice. Unlike editing config.yaml by hand, this is saved with the addon and kept on
  every future regeneration instead of getting silently dropped the next time config.yaml is
  rewritten.

## 0.1.16

- Fix: `EXPOSE ${PORT}` (a variable reference, resolved by Docker at build time from a preceding
  `ENV`/`ARG` declaration) was silently dropped — the port parser only understood a literal
  integer. This is a common pattern for apps whose listening port is also runtime-configurable
  (e.g. navidrome's `ENV ND_PORT=4533` + `EXPOSE ${ND_PORT}`), so the generated addon had no port
  mapping at all even though the option itself (`ND_PORT`) was correctly detected. EXPOSE now
  resolves `${VAR}`/`$VAR` against ENV/ARG declared earlier in the same Dockerfile.

## 0.1.15

- Fix: 0.1.14's `environment:` templating turned out not to be a real Home Assistant feature —
  confirmed against the official docs, `environment:` only ever sets static values, so the
  Supervisor was passing the literal string `{{ options.ADMIN_PASS }}` straight into the container
  unrendered (visible via `docker exec ... env`). Replaced it with the actual correct mechanism:
  when an addon has options, its entrypoint is now wrapped with a small script
  (`addon-forge-entrypoint.sh`) that reads `/data/options.json` itself and exports each value as a
  real env var before handing off to the app's own original ENTRYPOINT/CMD, preserved exactly as
  detected from its Dockerfile. Verified against a real container: `/proc/1/environ` now shows the
  configured values, and the app boots past its previous "ADMIN_USER and ADMIN_PASS are not set"
  failure. Not supported for a `FROM scratch` final stage (no shell exists there) — DOCS.md says so
  explicitly for addons that hit it, rather than silently leaving options undelivered.
- Fix: `git clone` on this addon's own host previously respected that host's `core.autocrlf`
  setting, which (on a Windows dev machine, less relevant for the Linux container this addon
  actually ships as) could rewrite a source repo's own LF-only scripts to CRLF and break their
  shebang line at container runtime. Cloning now always forces `core.autocrlf=false`.

## 0.1.14

- Fix: generated addons never actually delivered configured options to the container as
  environment variables. Home Assistant only writes options to `/data/options.json` — the
  packaged app (never written with that convention in mind) needs config.yaml's `environment:`
  section to map each option to an env var via `{{ options.KEY }}` templating. This was missing
  entirely, so any required option (e.g. subwave's `ADMIN_USER`/`ADMIN_PASS`) silently never
  reached the app, regardless of what was set in the addon's Configuration tab. Every generated
  option now gets a matching `environment:` entry automatically.

## 0.1.13

- Fix: env vars documented only in `.env.example` (never in the Dockerfile or compose
  `environment:` — common when a container loads its config via `env_file: ./.env`) were read but
  never actually used, so the generated addon had no way to collect them. `.env.example`'s active
  (non-commented) `KEY=value` lines are now parsed as option candidates, filling in anything the
  Dockerfile/compose didn't already cover. Commented-out "optional, uncomment to override" lines
  are still skipped — their intent is too ambiguous to treat as real candidates.
- Fix: `ADMIN_PASS`-style names (short for "password", not just the literal word "PASSWORD")
  weren't recognized as passwords and got flagged for manual review every time. Broadened the
  match to any name containing "PASS".

## 0.1.12

- New: repos with no Dockerfile at their root (common for multi-service stacks — one Dockerfile
  per component instead of a single buildable one) no longer silently generate a broken addon.
  Analyze now lists any Dockerfiles found elsewhere in the repo and lets you pick one to use; the
  choice is remembered for future regenerations. If none exist anywhere, generation fails with a
  clear reason instead of leaving the Supervisor to report a bare "dockerfile is missing".

## 0.1.11

- Fix: creating a branch-tracked addon recorded the literal branch name (`main`) as its current
  ref instead of resolving it to the branch's actual commit SHA. Since neither "main" nor a real
  SHA parses as a version, every subsequent "Check now" treated that as a change and bumped the
  version, even with nothing new upstream. Creation now resolves and stores the real SHA.

## 0.1.10

- Changed: branch-tracked addons no longer get the commit SHA baked into their `version` (first
  `0.1.0+<sha>`, then `0.0.0-<sha>` — both were workarounds for the same underlying issue). The
  version now just increments its patch number on every detected update (0.1.0 → 0.1.1 → 0.1.2...);
  the exact commit it was built from is already tracked separately in the addon's own record
  (visible on its detail page next to the version) and doesn't need to live in the version string.

## 0.1.9

- Fix: the version generated for branch-tracked addons (e.g. `0.1.0+6edb816`) used a `+` character,
  which is valid semver build metadata but not a valid Docker tag character — the Supervisor's
  build failed with `invalid reference format`. Branch-tracked addons now get a plain
  `0.0.0-<shortsha>` version instead. Addons already staged/published with a `+` in their version
  before this update won't fix themselves until the tracked branch moves again; "Stop managing" +
  re-add regenerates them immediately with a valid version.

## 0.1.8

- New: generated addons now get a real description instead of just the repo name — the README
  excerpt heuristic skips past the title heading (which usually just repeats the project name) to
  find the first actual descriptive sentence.
- New: generated addons pick up an icon/logo from the source repo when one is found (checked at
  common paths like `public/`, `assets/`, `static/`, `.github/`, then a filename scan for
  `*icon*`/`*logo*`). Only real PNG/JPEG files are used — SVG-only favicons aren't converted, so
  some repos still won't get one; that's an unchanged, safe fallback to Home Assistant's default
  icon, not a failure.

## 0.1.7

- Fix: checking for updates on a branch-tracked addon (no tags, tracking e.g. `main`) always failed
  with `Remote branch <sha> not found in upstream origin`. The scheduler was passing the resolved
  commit SHA to `git clone --branch`, which only accepts real branch/tag names, not bare SHAs.
  Regeneration now clones the tracked branch itself and records the resolved SHA separately as the
  version/ref shown in config.yaml, DOCS.md and CHANGELOG.md.

## 0.1.6

- Docs: added an explicit warning on the "Add addon" form (and in generated addons' own DOCS.md
  when ingress is enabled) that ingress only works if the packaged app is itself ingress-aware —
  most self-hosted apps aren't, and will look broken under ingress even though the container runs
  fine. This isn't fixable by addon-forge; it's a property of the app being packaged.

## 0.1.5

- Fix: the LLM `api_key` option (and the scheduler's `check_interval_hours`) never actually bound
  from config.yaml/options.json — .NET's configuration binder matches property names
  case-insensitively but doesn't strip underscores, so `api_key` never matched `ApiKey`. The
  Settings page always showed "not set" regardless of what was configured, and the LLM client was
  silently calling out with no key. Both options now bind correctly.

## 0.1.4

- Fix: 0.1.3 fixed links/redirects to the dashboard but missed the actual "Check now"/"Stop
  managing" form buttons on the dashboard itself — their action URL still resolved to the ingress
  prefix followed directly by `?handler=...` with no `/` boundary, which the ingress proxy 404s on
  (confirmed against a real Supervisor: the browser was navigating to exactly
  `.../api/hassio_ingress/<token>?slug=...&handler=Forget`). Both buttons now post to an explicitly
  slash-terminated URL.

## 0.1.3

- Fix: behind Home Assistant's ingress proxy, any link/redirect back to the dashboard ("/")
  resolved to the bare ingress prefix with no trailing slash, which the proxy 404s on — this hit
  every "Stop managing"/"Check now" action and the nav links back to the dashboard. Root-page URLs
  are now built with an explicit trailing slash, and `RouteOptions.AppendTrailingSlash` covers
  every other generated link.
- Hardening: pinned the antiforgery and TempData cookies to `Path=/` instead of the framework's
  default of scoping them to the ingress-prefixed PathBase, removing a class of cookie-scope
  mismatches under ingress.

## 0.1.2

- Fix: generated addons only included the `Dockerfile`, not the rest of the source repo, so any
  build that `COPY`s other files (requirements.txt, package.json, source dirs...) failed on the
  Supervisor. Generated addons now get the full repo tree as their build context.
- Fix: build/runtime plumbing env vars (`PATH`, `PYTHONUNBUFFERED`, locale vars...) no longer show
  up as generated addon options — they were never meant to be user-configurable.

## 0.1.1

- Fix: the runtime image was missing `git`, so analyzing/cloning a source repo failed with
  "No such file or directory". Added `apk add git` to the Dockerfile.

## 0.1.0

- Initial release: analyze a source repo, generate a Home Assistant addon, watch it for upstream
  updates, and stage or auto-write regenerated files on a schedule. No Supervisor install/update
  calls — every install/update step in Home Assistant is manual.
