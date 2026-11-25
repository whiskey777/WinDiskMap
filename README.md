# WinDiskMap

English | [Русский](README.ru.md)

[![ci](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml) [![codeql](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml) [![Download](https://img.shields.io/github/v/release/whiskey777/WinDiskMap?label=Download&style=flat&labelColor=24292e&color=28a745)](https://github.com/whiskey777/WinDiskMap/releases/latest) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%2FServer-0078D6?style=flat&labelColor=24292e)

Console disk space analyzer for Windows. Displays a directory tree in the terminal, calculates file and folder sizes, exports results to CSV, and supports remote scanning over WinRM.

## Description

WinDiskMap displays the directory structure and object sizes in the terminal. The tree is built during the scan and updated as data arrives. The privileged mode reads `$MFT` directly; the portable mode walks directories through Win32. Results can be exported to CSV or obtained from a remote host using the same calculation engine.

## Features

- **TUI with live updates** — the tree is built during traversal; the header shows the current phase and counters; Ctrl+C exits cleanly and SGR mouse input is supported (wheel, click).
- **Two collection modes** — direct `$MFT` reading (requires administrator rights and a local NTFS volume) and directory traversal (no elevated rights, UNC, non-NTFS). Both modes produce identical figures.
- **Remote scanning over WinRM** — only the net48 build of `WinDiskMap.exe` is copied to the remote host; data is streamed as NDJSON; the tree and CSV are built locally.
- **CSV export** — UTF-8 with BOM, comma separator, streaming writes, filters and depth limit.
- **Headless mode and logging** — `--headless` for scripts, `--log <file>` for diagnostics.
- **Two interface languages** — ru/en, selected from the operating system locale.

## Contents

- [Description](#description)
- [Features](#features)
- [Installation](#installation)
- [Usage](#usage)
- [TUI keys](#tui-keys)
- [CSV](#csv)
- [Documentation](#documentation)
- [Limitations](#limitations)
- [License](#license)

## Installation

Windows only. Ready-to-use archives are published in the [latest release](https://github.com/whiskey777/WinDiskMap/releases/latest); to build from source:

```powershell
git clone https://github.com/whiskey777/WinDiskMap.git
cd WinDiskMap
.\scripts\build.ps1
.\scripts\package.ps1 -SelfContained
```

The following archives appear in `artifacts\`:

| Archive | Contents | Recommended for |
|---|---|---|
| `WinDiskMap-portable-net48-v1.0.0.zip` | `WinDiskMap.exe` (~50 KB), uses .NET Framework 4.8 included in Windows | Windows 10/11/Server. This is also the executable that the WinRM transport copies to the remote host as the agent. |
| `WinDiskMap-portable-win-x64-selfcontained-v1.0.0.zip` | a single `WinDiskMap.exe` (~30 MB) with .NET 8 embedded | Use when .NET 8 is not installed and no dependency on the system .NET Framework is desired. |

The archives are functionally equivalent; they differ only in the runtime. They can be unpacked into any folder, require no installation and do not modify the registry. Each contains `WinDiskMap.exe`, `README.txt`, `LICENSE.txt` and `SHA256SUMS.txt`.

For source builds, after `build.ps1` use `src\WinDiskMap\bin\Release\net48\WinDiskMap.exe` (WinRM agent; .NET Framework 4.8 is part of the OS) or `src\WinDiskMap\bin\Release\net8.0\WinDiskMap.exe` (requires .NET 8 Runtime). For details, see [Build and packaging](docs/BUILD.md).

## Usage

```
WinDiskMap.exe <path> [options]
WinDiskMap.exe --remote <host> --path <path on host> [options]
```

| Option | Description |
|---|---|
| `--csv <file>` | export to CSV (instead of TUI) |
| `--headless` | no TUI: progress and final report in stdout |
| `--log <file>` | log with timestamps: phases, counters, errors |
| `--details` | with `--only dirs`: add one CSV row per file |
| `--depth <N>` | limit CSV row depth (`0` — root only) |
| `--only <mode>` | `dirs` (default), `files`, `tmp`, `dumps` |
| `--no-mft` | disable direct `$MFT` reading |
| `--lang <ru\|en>` | interface language (OS locale by default) |
| `--remote <host>` | scan a directory on a remote host over WinRM |
| `--remote-user <user>` / `--remote-pass <pass>` | explicit WinRM credentials |
| `--path <path>` | scan path (required with `--remote`/`--agent`) |
| `--agent` | internal mode: NDJSON to stdout (used by the host) |
| `--agent-local` | debug the protocol with a local agent process |
| `--debug-mft <mask>` | write attributes of MFT records with matching names to `--log` (`;` between masks) |
| `--help` | show help |

Examples:

```powershell
WinDiskMap.exe C:\Users
WinDiskMap.exe C:\ --csv big.csv --depth 2
WinDiskMap.exe C:\ --headless --csv dumps.csv --only dumps --log scan.log
WinDiskMap.exe \\server\share --headless
WinDiskMap.exe --remote hostB --path C:\Windows --csv remote.csv
WinDiskMap.exe --agent-local --path C:\Windows --csv agent.csv
```

Exit codes: `0` — success, `1` — invalid arguments, `2` — scan error, `3` — no terminal available for TUI. Full reference: [CLI](docs/CLI.md).

## TUI keys

| Key | Action |
|---|---|
| ↑ ↓, PgUp/PgDn, Home/End | navigation |
| ← / → | collapse / expand a folder (or move to the parent/first child) |
| Enter | folder — expand/collapse; file — open with the Windows association |
| `f` | cycle filters: folders → folders and files → tmp → dumps |
| `s` | cycle sorts: size ↓ → size ↑ → name ↑ → name ↓ |
| `e` | export the tree to CSV |
| Del | delete the file/folder after `y/N` confirmation (root cannot be deleted) |
| wheel / left click | scroll / move the cursor and expand a folder |
| `q`, Esc | exit |

## CSV

One format for all modes: UTF-8 with BOM, comma separator, no banner row. Headers and values (`Folder`, `File`, `Temp`, `Dump`) are always English, regardless of `--lang`.

```csv
File name,Size,On disk,Modified,Attributes,Files,Folders,Depth,Parent,Name,Category,SizeHuman,Percent,DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE
```

`--only dirs` (default) writes folder aggregates, `--details` adds files, `--depth` limits the depth. Field descriptions and calculation rules: [CSV format](docs/CSV-FORMAT.md).

## Documentation

- [Mechanics](docs/MECHANICS.md) — scan pipeline, MFT and traversal, size accounting, dates.
- [TUI interface](docs/TUI.md) — rendering, columns, filters, mouse, screen clearing.
- [CSV format](docs/CSV-FORMAT.md) — all columns, export modes, parsing examples.
- [CLI](docs/CLI.md) — all options, combinations, exit codes.
- [Remote scan architecture](docs/ARCHITECTURE.md) — process roles, NDJSON, WinRM.
- [WinRM setup](docs/REMOTE-SETUP.md) — checklist for the client and the host.
- [Accuracy verification](docs/BENCHMARK.md) — comparison method.
- [Build and packaging](docs/BUILD.md) — scripts, artifacts, versions, signing.
- [Continuous checks](docs/CI.md) — CodeQL, Dependabot, Dependency Review, branch protection.
- [Changelog](CHANGELOG.md).
- [Documentation index](docs/README.md).

## Limitations

- Windows only (P/Invoke `kernel32`/`user32`, direct `$MFT` reading).
- MFT mode requires administrator rights and a local NTFS volume; UNC paths always use traversal.
- WinRM requires host configuration; the NDJSON stream is optimized (2 MB chunks, decoding in C#); remote MFT mode was verified on Windows Server 2016.
- Archive signing is performed when SignPath is configured. Current archives are unsigned; corporate environments may show SmartScreen/EDR warnings (see [Build](docs/BUILD.md)).

## License

MIT — see [LICENSE](LICENSE).
