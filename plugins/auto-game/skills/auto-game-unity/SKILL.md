---
name: auto-game-unity
description: Inspect and automate a Unity Mono Player with Auto Game, covering the bundled scripts, snippets, reflection through Probe, frame-aware waits, and decompilation. Use once the auto-game skill has confirmed the target is a supported Unity Mono Player.
---

# Auto Game for Unity

The target is a Unity Player build, not the Editor, so `UnityEditor`, `AssetDatabase`, project assets, and Play Mode APIs do not exist in it. Command syntax is in `../auto-game/references/cli.md`.

## Inspect first

Without project source, behavior comes from loaded assemblies, reflection, scene objects, and components. The bundled scripts answer the common first questions. Pass one to `exec --file` and parameterize it with `--args`:

- `scripts/runtime-summary.cs` gives runtime and active-scene identity.
- `scripts/find-types.cs` finds loaded types by partial name.
- `scripts/describe-type.cs` lists the methods, properties, fields, and constructors of one type.
- `scripts/scene-hierarchy.cs` takes a bounded hierarchy snapshot.

## Snippets

A snippet is optional leading `using` directives followed by method-body statements. It receives `Args` as `AutoGameArgs`, `ctx` for bounded frame-aware waits, and `cancellationToken`. Read values with `Args.GetString`, `GetBoolean`, `GetInt64`, `GetUInt64`, `GetDouble`, `GetArray`, `GetObject`, or the indexer. Return compact JSON-friendly data.

`Probe` is in scope for reflection over the running game: `Probe.Type(name)` resolves a loaded type, `Probe.Components(name)` finds live components, `Probe.Get`/`Set`/`Call` read, write and invoke members, `Probe.Members(type)` describes one, and `Probe.Path(transform)` gives a scene path. Prefer it over hand-written reflection.

Game types are unknown at compile time, so a type, member, scene, or object that turns out to be missing is a result worth reporting rather than working around. A loaded game holds thousands of objects: cap output and filter early.

Snippets run on the Player's main thread, where sleeping or busy-waiting freezes the game. Span frames with `await ctx.WaitFrame()`, `WaitFrames`, `WaitSeconds`, or `WaitUntil`.

## Decompilation

`decompile` recovers source when reflection is not enough. Prefer one named assembly; `--all` writes the whole managed source tree.
