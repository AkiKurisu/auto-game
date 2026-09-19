---
name: auto-game
description: Use to inspect, decompile, or automate a game that is already running on Windows, without its project source. Identify the target first, then continue in the matching engine skill; Unity on the Mono backend is the engine supported today.
---

# Auto Game

`auto-game.exe` drives a game that is already running. Install it if it is not on `PATH`, then use a fresh shell:

```powershell
irm https://github.com/AkiKurisu/auto-game/releases/latest/download/install.ps1 | iex
```

Read `references/cli.md` before the first command.

## Identify the target

`auto-game list` returns every running game Auto Game can attach to, with its PID. Confirm which one the user means, then pass that PID to every later command. `auto-game status --pid <pid>` reads one process without attaching; `exec` attaches on its own.

A running game that `list` omits is not supported, and `status` on its PID fails with `error.code` `UnityTargetUnsupported`. Auto Game supports Unity on the Mono backend today, so tell the user their game is not supported yet and stop there: an IL2CPP game or another engine has no fallback path.

For a listed target, continue in the `auto-game-unity` skill (`../auto-game-unity/SKILL.md`).

## Changing a running game

Bounded changes the user asked for are fine. Ask first before deleting objects, loading or changing scenes, writing save data, advancing game progression, or applying a broad batch change.

An interrupted request may already have run inside the game, so read current state before retrying rather than replaying a mutating request after a timeout or disconnect.

Do not attempt anti-cheat bypasses, and do not use Auto Game with protected multiplayer software.
