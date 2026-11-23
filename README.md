# Zharp for Unity

C# exceptions with their stacks and the code that ran, crashes, hangs, sessions, scenes, traced
`UnityWebRequest`s and (optionally) logs from Unity games, sent to [Zharp](https://zharp.io).

A lean UPM package, `io.zharp.unity`, for **Unity 6.0 LTS and 6.3 LTS**. It has its own small C#
runtime — OTLP/JSON over `UnityWebRequest`, an outbox on disk, sessions — with no DLLs and nothing
for IL2CPP's linker to strip, so it works on every platform Unity builds for, **WebGL** included,
with Mono or IL2CPP.

## Install

**Git URL:** Window › Package Manager › **+** › *Install package from git URL…*:

```
https://github.com/zharp-io/zharp-unity-lib.git#0.1.0
```

or in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "io.zharp.unity": "https://github.com/zharp-io/zharp-unity-lib.git#0.1.0"
  }
}
```

**OpenUPM:** `openupm add io.zharp.unity`, or add the registry to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    { "name": "OpenUPM", "url": "https://package.openupm.com", "scopes": ["io.zharp"] }
  ],
  "dependencies": { "io.zharp.unity": "0.1.0" }
}
```

**Or** run `npm init zharp@latest` in the project folder: it finds `ProjectSettings/ProjectVersion.txt`,
adds the package to `Packages/manifest.json` and writes your key.

Then open **Edit › Project Settings › Zharp** and paste your app's key (from its Setup page in
Zharp). It's saved in `Assets/Zharp/Resources/ZharpSettings.json`, which ships with the game:

```json
{ "key": "zpk_…" }
```

That's all: Zharp starts before the first scene loads.

## What it reports

- **C# exceptions:** every exception Unity catches and logs (in `Update`, coroutines, callbacks,
  `async` methods), from `Application.logMessageReceivedThreaded`; exceptions that reach
  `AppDomain.UnhandledException` on other threads; and the ones you catch, with
  `Zharp.CaptureException(e)`. The stack goes as Unity wrote it
  (`Shop.Cart.Total () (at Assets/Scripts/Cart.cs:12)`). Each is sent once: the same error from
  the same place at most once a minute (an exception in `Update` happens every frame, and Unity
  sometimes logs one exception twice).
- **`Debug.LogError`:** a breadcrumb on the next exception (with warnings), or an error of its own
  with `logErrors` on.
- **IL2CPP release builds:** the build step sets **IL2CPP Stacktrace Information** to *Method Name,
  File Name, and Line Number*, so release stacks keep their files and lines (the build gets a little
  larger). Turn `il2cppLineNumbers` off to leave the setting alone.
- **The code that ran:** the build step uploads the game's scripts (`Assets/**/*.cs`) as a source
  bundle under the build's GUID, which every error carries as `zharp.build_id`; Zharp shows the
  lines around each of the game's frames from the exact build that failed. Needs `ZHARP_AUTH_TOKEN`
  at build time (below).
- **Crashes** (the player itself dying), sent on the next launch:
  - **iOS:** the build step adds the [Zharp iOS SDK](https://github.com/zharp-io/zharp-ios-lib) to
    the generated Xcode project, started for native crashes only (signals and Objective-C
    exceptions), and a Run Script phase that uploads the dSYMs. The iOS SDK needs iOS 15, so the
    Xcode project's deployment target is raised to 15.0 if it's lower (turn `nativeCrashes` off to
    keep yours).
  - **Windows:** Unity's crash handler writes a crash folder with a minidump; on the next launch the
    package uploads the newest one to Zharp, with that run's session, scene and player.
  - **Android, macOS, Linux:** a run that ended without quitting while in the foreground is sent as
    an `UncleanExit` with that run's session, scene and player. Readable native stacks for these
    come later (see *Not yet*).
- **Hangs:** the main thread not getting through a frame for 5 s (`hangThreshold`) is reported once
  per freeze (`ApplicationNotResponding`), from a watchdog thread, straight to disk. It's sent when
  the game gets through a frame again, or on the next launch if the player killed it frozen; a freeze
  that ended in a crash is reported as the crash only. Not on WebGL (no threads), nor while the game is in the
  background or a debugger is attached.
- **Sessions, device, screens:** `session.id` (a new one after 30 minutes in the background, or on
  `ClearUser()`), a random `device.id` kept in `Application.persistentDataPath`, the active scene as
  `screen.name` (with a `screen <name>` span each time one becomes active), and the player you set
  as `enduser.*`.
- **Requests:** `ZharpWebRequest.Send(request)` sends a `UnityWebRequest` as a client span with its
  method, URL, status and headers (secrets filtered), and adds `traceparent` so your server's trace
  continues it. Unity has no global hook for this, so it's opt-in per request.
- **Offline:** every batch is written to `persistentDataPath/zharp/outbox/` before it's sent and
  deleted once Zharp has it, so what couldn't be sent (no network, a crash, a quit) goes on the
  next launch.
- **Logs:** off by default. With `logs` on (or `ZHARP_LOGS=true`), `Debug.Log` lines are sent as
  info, warnings as warnings, errors as errors.

## API

```csharp
Zharp.SetUser("player-42", "ada@example.com", "Ada");   // enduser.* on everything
Zharp.ClearUser();                                       // sign-out: a new session
Zharp.SetTag("level", "forest-2");                       // an attribute on everything from now on

try { Save(); }
catch (Exception e) { Zharp.CaptureException(e, new Dictionary<string, object> { ["slot"] = 2 }); }
Zharp.CaptureError("Save failed: disk full");            // a handled error, with where it was called

Zharp.LogInfo("Checkout opened", new Dictionary<string, object> { ["items"] = 3 });  // also LogDebug, LogWarn, LogError

using (Zharp.StartSpan("load_level"))                    // a span; what starts inside is in it
{
    ...
}

// UnityWebRequest, traced: yield return it (or await it) as usual.
using var request = UnityWebRequest.Get("https://api.example.com/scores");
yield return ZharpWebRequest.Send(request);

// A scene load as a span ("scene.load Level2"), until the scene is ready.
yield return Zharp.LoadSceneAsync("Level2");

Zharp.SetScreen("Inventory");                            // the screen, when a scene doesn't say it
Zharp.Flush();                                           // send what's waiting now
```

## Settings

**Project Settings › Zharp**, saved in `Assets/Zharp/Resources/ZharpSettings.json`. Each can be
overridden by an environment variable (desktop players and the Editor), or in code with
`autoStart` off and `Zharp.Start(options => …)`.

| Setting | Environment | Default | |
|---|---|---|---|
| `key` | `ZHARP_KEY` | | the app's key |
| `environment` | `ZHARP_ENVIRONMENT` | `development` in the Editor | |
| `release` | `ZHARP_RELEASE` | Player Settings › Version | |
| `service` | `ZHARP_SERVICE` | the Product Name as Zharp names apps (`Shop Quest` → `shop-quest`) | |
| `logs` | `ZHARP_LOGS` | off | send `Debug.Log` lines |
| `logErrors` | | off | `Debug.LogError` as errors, not breadcrumbs |
| `tracing` | `ZHARP_TRACING` | on | spans (scenes, requests, yours) |
| `tracesSampleRate` | | 1.0 | |
| `detectHangs` | `ZHARP_DETECT_HANGS` | on | |
| `hangThreshold` | | 5 s | |
| `nativeCrashes` | | on | iOS SDK in the Xcode project; Windows crash folder upload |
| `il2cppLineNumbers` | | on | the build step sets IL2CPP line numbers |
| `uploadSources` | | on | the build step uploads `Assets/**/*.cs` |
| `uploadSymbols` | | on | iOS dSYMs (from the Xcode build) |
| `applicationId` | `ZHARP_APP_ID` | | the app's ID, for the build step's uploads |
| `enabled` | `ZHARP_ENABLED` | on | |
| `autoStart` | | on | off: call `Zharp.Start()` yourself |
| `debug` | `ZHARP_DEBUG` | off | print what Zharp does |
| `endpoint` | `ZHARP_ENDPOINT` | `https://api.zharp.io/api/v1/otlp` | |

### Uploads at build time

The build step uploads the source bundle (and, from the Xcode build, the iOS dSYMs) when
`ZHARP_AUTH_TOKEN` is set: a workspace API key with the `artifacts:write` scope. It's a secret — set
it in CI or your shell, never in the project. Without it, the build log says what would have been
uploaded and the build carries on. A failed upload never fails a build.

```sh
export ZHARP_AUTH_TOKEN=zak_…   # and ZHARP_APP_ID, unless applicationId is in the settings
```

## WebGL

Exceptions, sessions, scenes, requests and logs work the same; batches are sent uncompressed
(`System.IO.Compression` isn't there) and the outbox lives in the browser's IndexedDB. No hang
detection (no threads). Your Zharp app must allow the game's origin, like any browser app.

## Not yet

- **Android native crashes** (IL2CPP and engine signals in `libil2cpp.so` / `libunity.so`) arrive
  with the Zharp Android SDK's NDK support. Until then a crash is an `UncleanExit`.
- **macOS and Linux native crashes**: Unity writes no dump there. They need Zharp's native SDK
  (Crashpad) as a native plugin. Until then a crash is an `UncleanExit`.
- **Windows symbols:** readable frames in Windows minidumps need the game's `GameAssembly.pdb` uploaded
  and Unity's `UnityPlayer.pdb` from Unity's symbol server; until then frames in them are addresses.
- The Unity Asset Store listing.

## Development

The repository is the package (`package.json` at the root); folders ending in `~` are ignored by
Unity. `Runtime/Core` is plain C# with no `UnityEngine`, tested outside Unity:

```sh
docker run --rm -v "$PWD:/src" -w "/src/CoreTests~" mcr.microsoft.com/dotnet/sdk:8.0 dotnet test
```

The edit-mode tests (`Tests/Editor`) and the e2e players (`E2E~/ShopQuest`: Linux IL2CPP, WebGL,
Windows, iOS, Android, each against the stand-in receiver `E2E~/receiver.py`) run on GitHub Actions
with [GameCI](https://game.ci), which needs the repository secrets `UNITY_LICENSE`, `UNITY_EMAIL` and
`UNITY_PASSWORD` (a Unity Personal licence).

## License

MIT
