# CLI reference

Use the executable from `PATH`, or its absolute local build path.

```text
auto-game list
auto-game status --pid <pid>
auto-game attach --pid <pid>
auto-game exec --pid <pid> (--code <csharp> | --file <path>) [--args <json>] [--background] [--yield-time <ms>]
auto-game wait --pid <pid> --execution <id> [--yield-time <ms>] [--terminate]
auto-game detach --pid <pid>
auto-game decompile --pid <pid> --output <dir> (--assembly <name.dll> | --all)
```

All output is a JSON envelope. Check `ok`; on failure inspect `error.code` and `error.message`. An execution result has a bridge `state`: `completed`, `failed`, `cancelled`, `running`, `unknown`, or `lost`.

`status` does not attach. `exec` attaches as needed. `detach` stops the managed bridge; the loaded native module remains mapped until the game exits.

The target must be a Windows x64 Unity Mono Player with `UnityPlayer.dll`, a loaded Mono DLL, and `<executable>_Data/Managed`. IL2CPP is not supported.
