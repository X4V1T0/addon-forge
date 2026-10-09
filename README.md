# addon-forge

A Home Assistant OS addon that generates *other* HAOS addons from third-party Docker repos, and
watches those repos for new releases. See [DOCS.md](DOCS.md) for what it does and doesn't do, and
[config.yaml](config.yaml) for its own addon manifest.

This repo doubles as a normal .NET solution and as the contents of the HAOS addon folder — that's
why `config.yaml`/`Dockerfile` live at the repo root next to `src/`, rather than in a subfolder.

## Project layout

```
addon-forge/
├── config.yaml, Dockerfile, DOCS.md, ...  # the HAOS addon itself
├── build.ps1 / build.sh                   # publishes src/AddonForge.Web into ./dist
├── src/
│   ├── AddonForge.Core/            # analysis, generation, versioning — no I/O, fully unit-testable
│   ├── AddonForge.Infrastructure/  # EF Core, git CLI wrapper, LLM client, registry client, filesystem publisher
│   └── AddonForge.Web/             # ASP.NET Core host: Razor Pages UI + background scheduler
└── tests/
    ├── AddonForge.Core.Tests/
    └── AddonForge.Infrastructure.Tests/
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `git` on PATH (addon-forge shells out to it instead of bundling a Git library)
- Docker, only if you want to build/test the actual addon image locally

## Running locally (outside HAOS)

```powershell
dotnet run --project src/AddonForge.Web
```

Outside a HAOS container there's no `/data/options.json` and no `/addons` folder, so:

- Configuration falls back to `src/AddonForge.Web/appsettings.Development.json` (edit the `llm`,
  `scheduler` and `github` sections there to point at a real or local LLM endpoint).
- The SQLite database and staging area go under `src/AddonForge.Web/data/` instead of `/data`.
- "Publishing" a generated addon writes to `src/AddonForge.Web/data/addons-dev/<slug>/` instead of
  `/addons/<slug>/`, so you can exercise the full add/update/publish flow without a real HAOS
  install nearby.

Both fallbacks are computed once at startup in `Program.cs` (`Directory.Exists("/data")` /
`Directory.Exists("/addons")`), so nothing needs to be configured to switch between the two modes.

Open the app (the console output prints the URL, typically `http://localhost:5000`) and try:
**Add addon** → point it at a small public repo with a `Dockerfile` (or, with Source set to
**Docker image**, an already-published image like `docker.io/library/redis:7`) → **Analyze** →
review the detected ports/volumes/options → **Confirm & write to store**. Then open its detail page
and hit **Check for updates now**.

## Running tests

```powershell
dotnet test
```

`AddonForge.Core.Tests` covers the deterministic logic (Dockerfile/compose parsing, `config.yaml`
generation, version comparison) with no filesystem or network dependencies.
`AddonForge.Infrastructure.Tests` covers the filesystem-backed staging/publish writer against real
temp directories.

## Building the addon image

addon-forge ships **precompiled binaries** — the Dockerfile does not run `dotnet build` or
`dotnet restore`. Publish first, then build the image:

```powershell
./build.ps1          # or ./build.sh on Linux/macOS
docker build -t addon-forge:local .
```

`build.ps1`/`build.sh` publish with `-r linux-musl-x64 --self-contained false`, which matters even
though the output isn't self-contained: it makes `dotnet publish` fetch the musl-linked SQLite
native library the Alpine-based runtime image needs (the default glibc build won't load there).

To cut a new release: bump `version` in `config.yaml`, add a `CHANGELOG.md` entry, re-run the build
script, and (if publishing the image anywhere) rebuild/push the Docker image.

## Deploying as a local addon on a real HAOS install

1. Get this folder onto the HAOS host's `/addons` share — e.g. via the **Samba share** addon, or
   `scp`/the **Terminal & SSH** addon. It should end up at `/addons/addon-forge/` (containing
   `config.yaml` at its root).
2. Run `./build.ps1` (or `build.sh`) locally first so `dist/` — and therefore the addon folder you
   copied over — actually has binaries in it; `dist/` is gitignored and not part of the repo.
3. In Home Assistant: **Settings → Add-ons → Add-on Store → ⋮ → Check for updates** (or reload the
   page) — addon-forge should appear under **Local add-ons**. Install it, enable **Show in
   sidebar**, and configure the `llm`/`scheduler`/`github` options from its Configuration tab.
4. From then on, addon-forge writes the addons *it* generates to the same `/addons` folder (via its
   own `addons:rw` map) — they show up the same way, under Local add-ons, after you check for
   updates.

Updating addon-forge itself works the same way: bump its version, rebuild `dist/`, copy the updated
folder over the existing one, and check for updates in HA.

## Known v1 limitations (by design, see DOCS.md)

- A git-sourced addon is amd64 only (Addon Forge builds it locally). A Docker-image-sourced addon
  can be as multi-arch as the image it points at.
- Docker-image sources only work with public images — no private registry authentication.
- No Supervisor API calls of any kind — refreshing the store and installing/updating addons is
  always a manual step in Home Assistant.

## License

[MIT](LICENSE)
