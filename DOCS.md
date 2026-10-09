# Addon Forge

Generates Home Assistant addons from third-party Docker repos, and watches those repos for new
releases. It never installs, starts, or updates an addon for you — it only writes files into the
local addon store and lets you take it from there in Home Assistant's own UI.

## What it does

1. **Add an addon** from either kind of source:
   - **Git repo**: paste a repo URL (with a `Dockerfile`, optionally a `docker-compose.yml`).
     Addon Forge clones it and works out ports/volumes/environment variables from its Dockerfile.
   - **Docker image**: paste an already-published image reference (e.g.
     `docker.io/library/redis` or `ghcr.io/linuxserver/nginx`) and a tag to track. Addon Forge
     inspects it directly against its registry — no clone, no build context — and works out the same
     ports/volumes/environment variables from the image's own manifest. If nothing it found needs
     delivering into the container beyond what the image already does (no options, no config file, no
     persistent/shared data), the Supervisor **pulls the image directly** with no build at all;
     otherwise a minimal Dockerfile wrapped around it is built, the same as a git-sourced addon.

   Either way, you get a preview of the addon it would generate. Some images declare no ports at all
   (common for one built around Docker's own `network_mode: host`, e.g. a reverse proxy) — for those,
   add ports manually and/or turn on "Run on the host network" before confirming, or the generated
   addon won't be reachable. Confirming writes `config.yaml`, `DOCS.md` and `CHANGELOG.md` (plus
   `Dockerfile`, when one is needed) straight into the local addon store. Go to **Settings → Add-ons →
   Add-on Store → Check for updates** in Home Assistant afterwards to see it under Local add-ons.
2. **Watch for updates**: a background job checks each managed addon's source on a schedule — the
   git repo's tags/commits, or the image's registry tags, depending on how it was added. When it
   finds a newer version, it regenerates the addon's files. Depending on the addon's update policy,
   those files are either written straight to the local addon store (**Auto**) or held for you to
   review and approve first (**Manual**).
3. **You install/update in Home Assistant.** Addon Forge never calls the Supervisor to do that —
   only you, from the Add-on Store, do.

## Configuration

| Option | Description |
|---|---|
| `llm.endpoint` | Base URL of an OpenAI-compatible chat completions API. Works with OpenAI itself or a local Ollama/LM Studio instance. |
| `llm.api_key` | API key for the endpoint above. Leave empty for a local endpoint that doesn't need one — LLM-assisted schema suggestions are simply skipped and left for manual review. |
| `llm.model` | Model name to request. |
| `scheduler.check_interval_hours` | How often to check managed addons' source repos for updates. |
| `github.token` | Optional. Only used for `github.com` HTTPS clones, so private repos work. Public repos don't need this. |

Changing any of these requires restarting the addon.

## Persistent storage

Addon Forge needs read-write access to Home Assistant's local addon store, via this addon's
`addons:rw` map — that's how it writes the addons it generates to where Home Assistant can see
them. It uses its own `/data` directory (every addon gets one automatically) for its SQLite
database and its own staging area.

## What it deliberately does not do (v1)

- Install, start, or update any addon — including itself.
- Refresh the Home Assistant addon store on your behalf — you trigger that.
- Multi-architecture builds for a **git-sourced** addon — amd64 only, since Addon Forge builds
  those itself. A **Docker-image-sourced** addon can offer every architecture the published image
  itself does (Supervisor pulls it, or builds from it, per architecture — nothing here has to
  cross-compile).
- Authenticate to a private image registry — Docker-image sources only work with public images.
- Persist or share a directory that a **published image itself** declares as a `VOLUME`. That
  declaration is inherited from the image and can't be removed by anything built on top of it, so
  Docker mounts a volume there and a mount point can't be replaced by a symlink. Addon Forge refuses
  to generate such an addon and says so; point the app at another directory (most have a setting or
  environment variable for it, e.g. geodata's `GEODATA_DIR`) and persist/share that one.
  A git-sourced addon's own `VOLUME` lines are not affected — those are removed.
