# Hooking

SOCD: Snappy Tappy

---

## Building

```bash
dotnet publish Hooking.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=none -o publish
```

The native worker is compiled automatically when `Native/HookingWorker.exe` is
missing. To force a rebuild:

```bash
dotnet build -p:RebuildNativeWorker=true
```

Requires the .NET 10 SDK and MSVC (Visual Studio 2022 or 2026 with the C++
workload). `Native/build-worker.cmd` locates `VsDevCmd.bat` on its own.

## Where things are stored

Everything lives in `%APPDATA%\Hooking\`:

- `settings.json` — profiles, key pairs, options
- `runtime\HookingWorker.exe` — extracted copy of the engine

---

## Limits worth knowing

- **Kernel-level anti-cheat** (Vanguard, and EAC in kernel mode) filters
  injected events regardless of how they are produced, so no user-mode design
  can bypass that. The release-only model degrades gracefully there — movement
  keeps working, snap tap simply has no effect.
- **Extended keys** (arrows, numpad enter, right Ctrl/Alt, Insert/Home/PgUp)
  need `KEYEVENTF_EXTENDEDKEY`. The worker sets it from the real event flags.
- **Left and right modifiers** are kept apart by learning the virtual key from
  the genuine event rather than from `MapVirtualKey`, which is layout dependent.
- **Competitive rulesets** in some titles treat snap tap as prohibited
  automation. That is a rules question, not a technical one: use it where you
  are actually allowed to.
