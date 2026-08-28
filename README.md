# FDE — Foxhole Data Extractor

**Foxhole Data Extractor (FDE)** is a standalone, server-oriented tool for extracting structured game data and UI icons directly from a local Foxhole installation.

It automatically installs or updates Foxhole through SteamCMD, reads the game's Unreal Engine packages using CUE4Parse, builds a JSON catalog, and exports icons as PNG files.

FDE can also load optional third-party PAK files placed manually in `mods/`, allowing alternative assets such as clean UI icons to be exported alongside the original game assets.

## Disclaimer

It is recommended not to run Foxhole while an extraction is in progress to avoid potential file access conflicts or inconsistent results.

FDE only reads the game's PAK files stored on disk.

## Features

- Automatically installs and updates Foxhole using SteamCMD
- Keeps the game installation in a persistent Docker volume
- Reads Foxhole `.pak` files using CUE4Parse
- Extracts Unreal Engine package data to JSON
- Builds a structured `catalog.json`
- Resolves Blueprint inheritance and common game data
- Exports original Foxhole icons as PNG
- Supports optional modded/clean icons from manually supplied PAK files
- Uses item `CodeName` values for exported icon filenames
- Detects Foxhole build changes to avoid unnecessary extraction
- Can run periodically as a Docker Compose watcher
- Does not redistribute Foxhole or third-party PAK files

## Pipeline

The main extraction pipeline is:

```text
SteamCMD
    ↓
Foxhole
    ↓
War/Content/Paks
    ↓
CUE4Parse
    ├── raw JSON data
    ├── catalog.json
    └── icons/original/
```

Optional PAK files can be placed in `mods/`:

```text
mods/*.pak
    ↓
CUE4Parse
    ↓
icons/clean/
```

Optional PAK files are **never downloaded automatically**.

You must obtain them yourself from their respective official sources and manually place them in the `mods/` directory.

FDE does not redistribute Foxhole game files or third-party mod files.

## Requirements

- Docker
- Docker Compose
- A Steam account that owns Foxhole
- Enough disk space for the Foxhole installation and extracted data

SteamCMD and the .NET runtime required by the extractor are included in the Docker image.

## Installation

Clone the repository:

```bash
git clone https://github.com/C1ach0/fde.git
cd foxhole-data-extractor
```

Create your environment configuration:

```bash
cp .env.example .env
```

Edit `.env` and provide your Steam credentials:

```env
STEAM_APP_ID=505460
STEAM_USER=your_steam_username
STEAM_PASSWORD=your_steam_password

FOXHOLE_GAME_DIR=/game
OUTPUT_DIR=/output
STATE_DIR=/state
MODS_DIR=/mods

CHECK_INTERVAL_SECONDS=21600
```

The `.env` file is ignored by Git and must never be committed.

Build the Docker image:

```bash
docker compose build
```

Then initialize or update the Foxhole installation:

```bash
docker compose run --rm extractor update
```

## Steam Authentication and Steam Guard

Foxhole normally requires a Steam account that owns the game.

FDE uses authenticated SteamCMD sessions to download and update the game.

The `extractor` service has interactive stdin/TTY enabled. During the first authenticated login, SteamCMD may request a Steam Guard code directly in the terminal.

Enter the code only when SteamCMD asks for it.

You can explicitly initialize the Steam session with:

```bash
docker compose run --rm extractor steam-login
```

Steam authentication data is persisted using Docker volumes:

```text
steam_config
steam_home
```

Later executions can therefore normally reuse the authenticated machine/session without requiring Steam Guard again.

Do **not** put a Steam Guard code in `.env`. Steam Guard codes are temporary and should only be entered interactively when requested by SteamCMD.

## Usage

### Update and extract when necessary

```bash
docker compose run --rm extractor run
```

This checks the installed Foxhole build and performs extraction when required.

### Force regeneration

```bash
docker compose run --rm extractor force
```

This is useful after manually adding, replacing, or updating a PAK in `mods/`.

### Update Foxhole only

```bash
docker compose run --rm extractor update
```

### Extract only

```bash
docker compose run --rm extractor extract
```

This uses the currently installed Foxhole files without running a Steam update first.

### Steam login

```bash
docker compose run --rm extractor steam-login
```

### Display information

```bash
docker compose run --rm extractor info
```

### Run the automatic watcher

```bash
docker compose --profile watcher up -d watcher
```

The watcher periodically checks for updates according to:

```env
CHECK_INTERVAL_SECONDS=21600
```

Make sure Steam authentication works correctly before enabling unattended operation.

## Persistent Game Installation

The Foxhole installation is stored in the persistent Docker volume used by `/game`.

This is important because SteamCMD does **not** need to download the entire game every time FDE runs.

After the initial installation, SteamCMD can update the existing installation when a new Foxhole build is available.

## Optional Clean / Modded Icons

FDE can mount additional Unreal Engine PAK files as optional asset sources.

For example:

```text
foxhole-data-extractor/
├── mods/
│   └── War-WindowsNoEditor_CleanIconsEssential_FULLPACKAGE_v2.5.0.pak
├── output/
├── src/
├── compose.yml
└── ...
```

PAK filenames are not hardcoded. Compatible `*.pak` files placed in `mods/` are discovered as optional asset sources.

After adding or replacing a PAK, force a new extraction:

```bash
docker compose run --rm extractor force
```

The mod itself is **not included with FDE** and must be downloaded separately from its original source.

## Output

Generated files are written to `output/`:

```text
output/
├── catalog.json
├── version.json
├── changes.json
├── raw/
└── icons/
    ├── original/
    │   ├── AAAmmo.png
    │   ├── BasicMaterials.png
    │   └── ...
    └── clean/
        ├── AAAmmo.png
        ├── BasicMaterials.png
        └── ...
```

### `catalog.json`

`catalog.json` contains the extracted Foxhole catalog.

The extractor reads Foxhole Blueprint data, resolves relevant inheritance, and combines common game data into catalog entries.

A simplified entry may look like:

```json
{
  "ObjectPath": "War/Content/Blueprints/ItemPickups/BPAAAmmoPickup",
  "CodeName": "AAAmmo",
  "DisplayName": "...",
  "Description": "...",
  "ItemCategory": "HeavyAmmo",
  "Icon": "...",
  "Icons": {
    "Original": "icons/original/AAAmmo.png",
    "Clean": "icons/clean/AAAmmo.png"
  }
}
```

The exact fields available depend on the data exposed by the current Foxhole build.

### `raw/`

The `raw/` directory contains JSON representations of the Unreal Engine packages selected during extraction.

These files are intentionally retained so the catalog mapping can evolve without losing access to the underlying extracted data.

### `icons/original/`

Contains icons decoded from the official Foxhole game packages.

```text
icons/original/BasicMaterials.png
```

### `icons/clean/`

Contains corresponding icons obtained from optional PAK files when available.

```text
icons/clean/BasicMaterials.png
```

The extractor keeps original and optional assets separate.

A consumer can therefore choose its own fallback behavior:

```text
clean ?? original
```

FDE intentionally does not copy original icons into `clean/` when no clean variant exists.

## Project Structure

```text
foxhole-data-extractor/
├── config/
│   └── extraction.json
├── mods/
├── output/
│   ├── icons/
│   │   ├── original/
│   │   └── clean/
│   └── raw/
├── scripts/
├── src/
│   └── FoxholeDataExtractor/
├── state/
├── .env.example
├── compose.yml
├── Dockerfile
├── LICENSE
└── README.md
```

## Technology

FDE primarily uses:

- **Docker / Docker Compose** for reproducible server deployment
- **SteamCMD** for Foxhole installation and updates
- **.NET** for the extraction application
- **CUE4Parse** for reading Unreal Engine packages
- **CUE4Parse-Conversion** for asset conversion
- **SkiaSharp** for PNG image processing

The Windows version of Foxhole is installed through SteamCMD because the extractor operates on the game's Windows PAK files.

## Inspiration

Part of the catalog extraction approach was inspired by **FIR (Foxhole Inventory Report)** by GICodeWarrior.

FIR demonstrated how Foxhole Blueprint data and related game structures can be combined into a useful item catalog.

FDE is a separate project and uses CUE4Parse directly to read the installed game packages.

## AI-Assisted Development

This project was developed with the assistance of AI tools, including **OpenAI's ChatGPT**.

AI assistance was used during parts of the design, implementation, debugging, documentation, and research process.

The generated and suggested code was reviewed, tested, and integrated as part of the development process. AI-assisted development does not change the licensing or ownership of third-party libraries, Foxhole assets, or other external content used by this project.

## Legal Notice

FDE is an unofficial community project and is not affiliated with, endorsed by, or sponsored by the developers or publishers of Foxhole.

Foxhole, its game data, artwork, textures, icons, and other assets remain the property of their respective rights holders.

Third-party PAK files and mods remain subject to the licenses and terms established by their respective authors.

This repository does **not** contain or redistribute:

- Foxhole game PAK files
- extracted Foxhole assets
- Steam credentials
- third-party mod PAK files

Users are responsible for ensuring that their use and distribution of extracted data and assets complies with applicable licenses, terms of service, and copyright law.

## License

The source code of **FDE — Foxhole Data Extractor** is licensed under the [MIT License](LICENSE).

This license applies only to the source code of this project.

It does not grant rights to Foxhole assets, third-party PAK files, Steam content, or other externally owned material.