# SelfCam

A client-side, **practice-only** BepInEx mod for **STRAFTAT** that adds a corner picture-in-picture
showing **your own character** — drop a camera in the world and watch your body, animations and
cosmetics, with an adjustable replay delay so you can review how a movement looked.

Read-only and vanilla-compatible: it sends no network messages and changes no gameplay state.

## Controls (rebindable via the BepInEx config / mod menu)

| Key | Action |
|-----|--------|
| `O` | Toggle the PIP on/off |
| `P` | Drop the camera at head level (glowing marker shows where) |
| `L` | Lock/unlock — track your head, or hold the current view |
| `[` `]` | Decrease / increase the replay delay (0 = live, up to 5s in the past) |
| `K` | Save a screenshot of the self-cam to your Pictures folder (full-res when live) |

FOV and all keys are configurable.

## Fairness / scope

Active **only in the tutorial and exploration/sandbox (test) maps**; it **force-disables itself in
all real matches** (public matchmaking *and* private custom matches on real maps). This is
fail-closed — once you're in a real match the game gives no reliable, client-synced way to tell a
private lobby from matchmaking, so it stays off rather than risk being a competitive advantage.

## Install (players)

Install via a mod manager (r2modman / Thunderstore Mod Manager); it pulls in BepInEx automatically.

## Build (developers)

- Requires the .NET SDK and a local STRAFTAT install (the build references the game's DLLs).
- Set your game path in `QuarterViewSelfCam/Directory.Build.local.props`:
  ```xml
  <Project><PropertyGroup>
    <GameDir>/path/to/steamapps/common/STRAFTAT/</GameDir>
  </PropertyGroup></Project>
  ```
- `dotnet build -c Release QuarterViewSelfCam/QuarterViewSelfCam.csproj` builds and deploys the DLL
  into the game's `BepInEx/plugins/`.
- `./pack.sh` produces a Thunderstore-style package zip in `dist/`.

BepInEx compile-time DLLs are vendored in `libs/` so the build doesn't depend on where BepInEx is
installed at runtime.
