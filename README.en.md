# StutterLens

English | [中文](README.md)

StutterLens is a lightweight background tool for Windows that detects in-game stutters, works out what most likely caused each one, and tells you what to do about it. When the game closes, it opens a report in your browser.

- **Finds the cause.** Each stutter is matched against what the system was doing at that moment: driver latency, a background program, memory, disk, or the game itself.
- **Suggests fixes.** Every cause comes with steps, sorted from easy to advanced.
- **Leaves the game alone.** No injection, no reading game memory, no kernel driver. When an anti-cheat is present it switches to an even more conservative mode.
- **Stays out of the way.** Measured overhead is about 0.2% CPU and 85 MB of memory (i7-10700K, deep mode).

The report UI and suggestions are currently in Chinese. Sample report: [docs/sample-report.html](docs/sample-report.html).

## Getting started

1. Download `StutterLens-x.y.z-win-x64.zip` from [Releases](../../releases) and extract it anywhere.
2. Run `StutterLens.exe` and accept the UAC prompt. Reading frame events requires administrator rights.
3. Play as usual. StutterLens picks up the game automatically.
4. When the game exits, the report opens in your browser. You can also press Ctrl+C or close the window to finish early.

Reports are saved to `%LOCALAPPDATA%\StutterLens\Reports`.

> Windows SmartScreen may warn about an unrecognized app because the binary is not code-signed yet. Choose "More info → Run anyway", or build from source.

**Requirements:** Windows 10 1809+ or Windows 11, x64, administrator rights.

## Anti-cheat safety

In every mode StutterLens does **not**:

- open a handle to the game process (no `OpenProcess`), read or write game memory, or inject DLLs
- draw overlays or hook graphics APIs or input
- install a kernel driver (no WinRing0-style hardware access drivers)

All data comes from public Windows interfaces, the same kind used by Task Manager, Performance Monitor and PresentMon.

| Mode | Data sources | When |
|---|---|---|
| Safe | Performance counters, process list, graphics Present events | An anti-cheat process or driver is running, or the game is on the known anti-cheat list |
| Deep | The above plus kernel ETW (driver DPC/ISR, disk I/O) | Single-player games with no anti-cheat seen 15 seconds after launch |

Kernel tracing stops the moment an anti-cheat appears, even if `--mode full` was requested. No third-party tool can get an official guarantee from anti-cheat vendors; StutterLens minimizes risk by only using public interfaces and taking the most conservative path.

## Privacy

Everything runs locally. StutterLens makes no network connections and collects nothing. File paths in the JSON report have your Windows user name removed, so reports are safe to share when asking for help.

## Command line

```bash
StutterLens.exe                          # monitor in the background (default)
StutterLens.exe check                    # system settings check, no admin needed
StutterLens.exe --process game.exe       # watch one specific game
StutterLens.exe --mode safe              # force safe mode
StutterLens.exe --include-background     # count stutters while the game is not focused
StutterLens.exe help                     # all options
```

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test StutterLens.sln -c Release
```

```bash
dotnet publish src/StutterLens -p:PublishProfile=win-x64
```

The self-contained single-file `StutterLens.exe` is written to `artifacts/publish/`.

## Feedback

False positives or missed causes are worth an [issue](../../issues/new/choose). Attaching the JSON report helps a lot. Knowledge-base additions (anti-cheat signatures, background programs, drivers) go in [KnowledgeBase.cs](src/StutterLens/Knowledge/KnowledgeBase.cs).

## License

[MIT](LICENSE)
