# SelfCam

A client-side, **practice-only** mod that adds a small picture-in-picture showing **your own
character** — so you can watch your movement, animations and cosmetics while you play.

You drop a camera in the world; it stays put and keeps looking at you (security-cam style), with an
adjustable **replay delay** so you can review how a movement looked a moment after you did it.

## Controls (rebindable in the config / mod menu)

| Key | Action |
|-----|--------|
| `O` | Toggle the PIP on/off |
| `P` | Drop the camera at head level (a glowing marker shows where) |
| `L` | Lock/unlock — track your head, or hold the current view (marker turns red when locked) |
| `[` `]` | Decrease / increase the **replay delay** (0 = live, up to 5s in the past) |
| `K` | Save a **full-resolution screenshot** of the self-cam view to your Pictures folder |

The PIP shows your full third-person body, head and cosmetics (suit, hat, etc.). FOV and keys are
configurable.

## Fairness / scope

This mod is **practice-only by design** and **read-only / vanilla-compatible** — it sends no network
messages and changes no gameplay state, so non-modded peers see no difference.

It is **active only in the tutorial and the exploration / sandbox (test) maps**, and **force-disables
itself in real matches** (public matchmaking *and* private custom matches on real maps). This is
fail-closed: once you're in a real match the game gives no reliable way to tell a private lobby from
matchmaking, so it stays off rather than risk being a competitive advantage.

## Install

Depends on BepInEx. Install via a mod manager (r2modman / Thunderstore Mod Manager) and it pulls in
the dependency automatically.

## Status

Early release (v0.1.0): placeable self-view + head tracking + lock + replay + cosmetics, with a
fail-closed practice gate.
