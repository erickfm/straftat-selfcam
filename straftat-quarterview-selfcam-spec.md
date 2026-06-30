# STRAFTAT Mod Spec — QuarterView SelfCam

**Working title:** QuarterView SelfCam
**Type:** BepInEx plugin (client-side, cosmetic/HUD)
**Target game:** STRAFTAT (Unity, Mono backend)
**Status:** Draft v1 — scoped for implementation
**Author:** (you)

---

## 1. Summary

A client-side mod that renders a small picture-in-picture (PIP) viewport in a corner of the screen showing the local player's own character model from a fixed **3/4 (three-quarter) perspective** — i.e. an angled, slightly elevated view from behind the player, looking down at roughly 30°. The purpose is to let the player see their own character, animations, cosmetics, and movement during play (and on the death/spectate screen), in a way the base game's locked first-person view does not allow.

This is a "see yourself" mirror-style camera. The default framing places the camera roughly **one player-model length behind and above** the local player, angled down and forward so the player model sits in frame from a back-three-quarter angle.

**Scope: practice contexts only.** This mod is a practice aid, not a match feature. It is **only active in the tutorial and in private/offline lobbies ("custom"), and hard-disables itself in matchmaking/ranked play.** Note that STRAFTAT has no offline-bot mode, so "practice" means the tutorial scene or a private lobby (solo or with a friend). Gating to these contexts removes any competitive-integrity concern — see §8.

---

## 2. Background & constraints

### 2.1 Verified internals (from decompiled game + real mod source)
Confirmed by reading `Assembly-CSharp` references and the open-source `Minimal Viewmodels Reborn` mod:

- **Engine:** Unity **2021.3.45**. Plugins target **netstandard2.1**.
- **Mod loader:** **BepInEx 5.4.21** (the `5.4.2100` Thunderstore pack). Harmony for patching.
- **Networking:** **FishNet** (`FishNet.Runtime.dll`). Player objects are `NetworkBehaviour`s; ownership is checked with `IsOwner`; setup runs in `OnStartClient`. **The mod must only act for the locally-owned player (`IsOwner`).**
- **Game assembly is publicized** (`BepInEx.AssemblyPublicizer`), so private fields/methods are directly accessible — no reflection needed.
- **Confirmed game classes/handles:**
  - `Settings.Instance` — singleton. Holds `Settings.Instance.localPlayer` (a `FirstPersonController`) and `Settings.Instance.qualitySetting` (int; `< 2` = medium/low graphics). **This is the clean way to get the local player** — no manual scene scraping needed.
  - `FirstPersonController` — the player controller (movement/FOV fields live here).
  - `PlayerSetup` — networked setup component. In `OnStartClient` it exposes (private, publicized) `Camera[] cameras`, `LayerMask highMask`, and `GameObject[] fpArms`. **`cameras[0]` is the main camera (culling mask = `highMask`); `cameras[1]` is the weapon/viewmodel camera.** The game already runs a multi-camera stack, so adding a third camera is consistent with how the game works.
  - `fpArms` are **first-person arms only** — see R-1 about full-body visibility.
- **Other libs present:** DOTween, Unity Post-processing v2 (`PostProcessLayer`), and `ComputerysModdingUtilities.dll` (the shared Straftat modding lib; provides the `[assembly: StraftatMod(isVanillaCompatible: true)]` attribute used to mark vanilla-compatible mods).
- **Config convention:** `config.Bind("Category.Sub", "Name", default, new ConfigDescription(..., new AcceptableValueRange<T>(min,max)))`. An external **in-game mod config menu exists** ("Configure your mods ingame!") that auto-renders BepInEx config entries, so live tweaking comes "for free" from well-bound configs — no custom UI required.

### 2.2 Constraints
- STRAFTAT is **first-person only** with **no offline/bot mode** and no native replay/spectator-freecam. Existing community mods: `FreecamSpectate` (death-cam free-fly) and `Minimal Viewmodels Reborn` (repositions FP viewmodel/arms only). **No existing mod renders the player's own full body in third person**, so the body-visibility path must be validated early (R-1).

**Hard constraints**
- Client-side only. No network messages. No change to hitboxes, movement, weapons, or any networked state. Act only when `IsOwner`.
- **Vanilla-compatible** — mark with `[assembly: StraftatMod(isVanillaCompatible: true)]`. A non-modded peer must observe no difference.
- Degrade gracefully: if expected objects/fields are missing after a game update, log and self-disable — never crash.

---

## 3. Goals / non-goals

**Goals**
1. Render the local player's own character model in a corner PIP from a configurable 3/4 perspective.
2. Default camera placement ≈ one player-model length behind + above the player, pitched down ~30°.
3. Camera follows the player smoothly during live play and on the death screen.
4. All key parameters exposed via the BepInEx config file (and live-editable via Mod Menu at runtime, matching the convention other Straftat mods follow).

**Non-goals (v1)**
- No enemy-tracking camera (camera framed on the opponent). The camera is **self-framed** only.
- No replay recording, no free-fly control, no multi-camera CCTV.
- No changes to the main viewport's perspective. First-person main view is untouched.
- No activity outside practice contexts — see §8.

---

## 4. Functional requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1 | A second `Camera` renders the scene from a 3/4 angle relative to the local player. | Must |
| FR-2 | That camera's output is shown in a corner PIP overlay on the HUD. | Must |
| FR-3 | The local player's own model is visible in the PIP (the main view normally culls/hides the local body — see Risk R-1). | Must |
| FR-4 | PIP corner, size, opacity, distance, height, pitch, yaw-offset, and FOV are all config-driven. | Must |
| FR-5 | A keybind toggles the PIP on/off at runtime (default config-defined, e.g. `F4`). | Must |
| FR-6 | Camera follows player position every frame with optional smoothing (lerp). | Must |
| FR-7 | Works on both the live combat view and the death/spectate view. | Should |
| FR-8 | Camera optionally rotates with the player's facing (yaw-follow) or stays world-fixed. | Should |
| FR-9 | Optional wall-collision pull-in so the PIP camera doesn't clip into/through geometry. | Could |
| FR-10 | Mod cleanly tears down its camera + RenderTexture on scene unload / disable. | Must |
| FR-11 | **Context gate:** the PIP is only created/shown in the tutorial scene and private/offline lobbies; it is force-hidden and inactive in matchmaking/ranked. | Must |

---

## 5. Camera geometry — the "3/4 perspective"

Define the PIP camera transform each frame relative to the local player root transform `P` (position) and the player's facing yaw `θ`.

```
Inputs (config):
  distance   d      default 1.6 m   (≈ one player-model length back)
  height     h      default 1.4 m   (above player origin)
  pitch      φ      default 30°     (downward tilt)
  yawOffset  ψ      default 35°     (rotate around player for the "3/4" angle,
                                     not straight-behind; gives the diagonal look)
  fov               default 50°
  followYaw  bool    default true   (camera orbits with player facing)
```

Placement (pseudo):
```
baseDir   = followYaw ? player.forward : worldForward
orbitDir  = Quaternion.Euler(0, θ + ψ, 0) * (-baseDir)   // behind + offset to the side
camPos    = P + orbitDir.normalized * d + Vector3.up * h
lookTarget = P + Vector3.up * (chestHeight ≈ 0.9 m)
camRot    = LookRotation(lookTarget - camPos)             // pitch falls out of this
```

The combination of **yawOffset (~35°)** + **pitch (~30°)** is what produces the classic three-quarter / over-the-shoulder-diagonal framing rather than a flat straight-behind chase cam. All four of `d, h, φ, ψ` are exposed so the user can dial anything from near-isometric to tight over-shoulder.

Smoothing: `camPos = Vector3.Lerp(prevCamPos, camPos, followSmooth * dt)` with `followSmooth` configurable (0 = instant snap).

---

## 6. Technical approach

### 6.1 Render path
1. Create a dedicated `GameObject` with a `Camera` component (`SelfCam`).
2. Match relevant settings from the main camera (clear flags, culling-ish, post-processing decision — see R-2) but render to a **`RenderTexture`** (e.g. 480×270 or config-sized) instead of the screen. Set `targetTexture`.
3. Create a HUD `Canvas` (Screen Space – Overlay) with a `RawImage` bound to that RenderTexture, anchored to the configured corner with configured size/opacity/margin.
4. Each `LateUpdate`, recompute the camera transform per §5 (LateUpdate so it follows after player movement/animation has resolved that frame).

### 6.2 Hooks (Harmony / BepInEx) — using verified handles
- **Get the local player directly:** `Settings.Instance.localPlayer` (a `FirstPersonController`). Its transform is the follow target. No scene scraping.
- **Hook setup to grab cameras + layers:** `[HarmonyPostfix]` on `PlayerSetup.OnStartClient`, guarded by `if (!__instance.IsOwner) return;` (mirrors how Minimal Viewmodels Reborn grabs `cameras`/`fpArms`). Read the publicized `Camera[] cameras`, `LayerMask highMask`, `GameObject[] fpArms` to learn the main camera's culling setup so the SelfCam can match/extend it.
- **Lifecycle:** create the SelfCam after `OnStartClient` (owner); re-evaluate the practice gate and visibility on scene load; tear down on scene unload / match end.
- **No gameplay patches.** Read-only access to transforms/cameras only.

### 6.3 Local-model visibility (THE critical unknown)
Confirmed: the game gives the owner **first-person arms** (`fpArms`) and renders the main view through `cameras[0]` with culling mask `highMask`. **What is NOT yet confirmed: whether the locally-owned player has a full third-person body mesh present and animating locally** (other clients clearly render this player's body, but the owner's copy may be absent, culled, or on a hidden layer). This is the make-or-break question for the whole mod.

Approaches, in order of preference, to be settled in the M0 spike:
1. If the full body exists locally and is merely layer-culled from `cameras[0]`, set the SelfCam's culling mask to **include** that layer (and exclude `fpArms`/viewmodel layers so the FP arms don't float in the PIP).
2. If the body mesh isn't instantiated locally, spawn a **render-only copy** of the player model (driven to match the player's transform/animation) on a dedicated layer only the SelfCam renders.
3. Worst case (no riggable body available locally), the feature isn't viable as specced and scope must change — hence the M0 go/no-go gate.

**Verify with dnSpy/dnSpyEx on the publicized `Assembly-CSharp.dll`** (inspect `PlayerSetup`, the player prefab, layer assignments) **plus an in-game test**, before any other milestone.

### 6.4 Teardown
On disable/scene-unload: release the RenderTexture (`.Release()` + destroy), destroy the camera GO and canvas, unsubscribe hooks. No leaks across matches.

---

## 7. Configuration (BepInEx config)

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | true | master toggle |
| `ToggleKey` | KeyboardShortcut | F4 | runtime show/hide |
| `Corner` | enum {TL,TR,BL,BR} | BR | screen anchor |
| `PipWidth` | int (px) | 480 | RenderTexture + display width |
| `PipHeight` | int (px) | 270 | 16:9 default |
| `PipOpacity` | float 0–1 | 1.0 | RawImage alpha |
| `Margin` | int (px) | 16 | from screen edge |
| `Distance` | float (m) | 1.6 | back distance (~1 model length) |
| `Height` | float (m) | 1.4 | vertical offset |
| `Pitch` | float (°) | 30 | downward tilt |
| `YawOffset` | float (°) | 35 | the "3/4" diagonal |
| `Fov` | float (°) | 50 | SelfCam FOV |
| `FollowYaw` | bool | true | orbit with player facing |
| `FollowSmooth` | float | 12 | lerp speed; 0 = snap |
| `CollisionPullIn` | bool | false | wall clip avoidance (FR-9) |
| `RenderScale` | float | 1.0 | perf vs sharpness |

Expose options to update at runtime (Mod Menu compatibility) like other Straftat mods do.

---

## 8. Scope gating / fairness (read before building)

This mod is **practice-only by design**, which sidesteps competitive-integrity questions entirely.

**Active contexts (allow):**
- Tutorial scene.
- Private / offline lobby ("custom") — solo or with a friend.

**Inactive contexts (force-disable):**
- Public matchmaking / ranked. The mod must detect this and keep the PIP fully inactive (camera not created, no RenderTexture, nothing drawn).

**Implementation notes for the gate:**
- **Open question — the exact "is this matchmaking vs private vs tutorial" signal is not yet confirmed from public source.** It must be found by inspecting `Assembly-CSharp.dll` in dnSpy: look for the lobby / game-mode / matchmaking manager types and any tutorial scene name, and/or read the Steam lobby metadata (FishNet + Steam transport). Candidate signals to evaluate during M0: active scene name (tutorial), a lobby "is private/friends-only" flag, or a matchmaking-state enum.
- Fail **closed**: if context can't be determined with confidence, treat it as ranked and keep the PIP off.
- Re-evaluate the gate on every scene load / lobby change, not just at startup.
- There is no enemy-tracking mode in this build. The camera is self-framed only.

Mod remains **read-only and vanilla-compatible** (no gameplay state changes), mirroring the framing accepted Straftat HUD mods use. Document the practice-only scope clearly in the Thunderstore README so users and the community understand it can't be used as a match advantage.

---

## 9. Risks & unknowns

| ID | Risk | Mitigation |
|----|------|-----------|
| R-1 | **Owner may have no full-body third-person mesh locally** (only `fpArms` confirmed); SelfCam could render a headless/empty player. | **Spike first** (§6.3): dnSpy `PlayerSetup`/player prefab + in-game test. If absent, fall back to a render-only model copy. Go/no-go. |
| R-2 | Third camera + post-processing may cost FPS in a fast movement game; note weapons render via main cam on `qualitySetting < 2`. | Modest default RenderTexture size + RenderScale; disable post on SelfCam; honor/branch on `Settings.Instance.qualitySetting`. |
| R-3 | Publicized field names (`cameras`, `highMask`, `fpArms`, `Settings.localPlayer`) can change on a game update. | Null-check everything; wrap hooks in try/catch; self-disable + log instead of crashing. |
| R-4 | Third-person rig may not animate locally for the owner. | Verify in the R-1 spike; if needed, drive the render-only copy from the controller's state. |
| R-5 | Camera clipping through walls looks bad. | Optional `CollisionPullIn` raycast from player to camPos (FR-9, Could). |
| R-6 | **Practice-context signal not yet identified** (§8) — gate could misfire. | dnSpy the lobby/matchmaking/tutorial types in M0; fail closed until a reliable signal is confirmed. |

---

## 10. Milestones

1. **M0 — Spike (de-risk R-1/R-4 + context gate):** Stand up a bare second camera + RawImage PIP; confirm the local model renders and animates in it. Also identify a reliable signal for "tutorial / private lobby vs matchmaking" so the context gate (FR-11) can fail closed. Go/no-go gate.
2. **M1 — Core cam:** 3/4 placement math (§5), follow in LateUpdate, basic config (distance/height/pitch/yaw/fov).
3. **M2 — HUD polish:** corner/size/opacity/margin, toggle key, smoothing.
4. **M3 — Lifecycle:** death-screen support, scene change teardown, no leaks.
5. **M4 — Robustness + config UX:** runtime/Mod Menu live updates, defensive discovery, optional collision pull-in.
6. **M5 — Ship:** Thunderstore package (manifest, README documenting fairness notes, icon), depends on BepInEx 5.4.2100.

---

## 11. Acceptance criteria

- With the mod installed and no other config changes, a PIP appears bottom-right showing the local player from a 3/4 angle ~1 model-length back and above, pitched ~30°, following the player during movement.
- **The PIP appears in the tutorial and in private/offline lobbies, and is fully inactive in matchmaking/ranked. If context is undetermined, the PIP stays off (fail closed).**
- Toggle key hides/shows the PIP; all §7 params change the view as described, live where supported.
- No gameplay state changes; non-modded peers observe nothing different.
- No crashes on death, respawn, map change, or match end; RenderTextures released (no memory growth across 10+ matches).
- If player/camera objects can't be found, the mod logs a warning and disables itself rather than throwing.

---

## 12. References (for the implementing engineer)

**Verified toolchain / deps**
- Unity **2021.3.45**; plugin target **netstandard2.1**.
- **BepInEx 5.4.21** + HarmonyX; `BepInEx.AssemblyPublicizer.MSBuild` to publicize `Assembly-CSharp`.
- Reference assemblies (from `STRAFTAT_Data/Managed/`): `Assembly-CSharp.dll` (publicized), `FishNet.Runtime.dll`, `ComputerysModdingUtilities.dll`, `DOTween.dll`, `Unity.Postprocessing.Runtime.dll`.
- Default game path: `C:\Program Files (x86)\Steam\steamapps\common\STRAFTAT\`.

**Reference mods (read these)**
- `Minimal Viewmodels Reborn` (kestrel) — open source; **the best reference**. Shows: `[assembly: StraftatMod(isVanillaCompatible: true)]`, `BepInPlugin` setup, `Configs.Init` binding pattern, the `PlayerSetup.OnStartClient` postfix grabbing `cameras`/`highMask`/`fpArms` with an `IsOwner` guard, and `Settings.Instance.localPlayer` / `qualitySetting` usage. (`github.com/kestrel-straftat/minimal-viewmodels-reborn`)
- `FreecamSpectate` (LeodisTaylor) — reference for death-cam free-fly + camera manipulation (decompiled source on its Thunderstore page).

**Inspection / community**
- **dnSpyEx** (or ILSpy) on the publicized `Assembly-CSharp.dll` — required to resolve the R-1 body-mesh question (§6.3) and the R-6 practice-context signal (§8).
- Straftat modding Discord and the Straftat Wiki "Mods and Resources" page for the dev-environment setup and the in-game mod config menu.
