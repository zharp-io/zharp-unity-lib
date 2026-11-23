# Changelog

## 0.1.0

First release, for Unity 6.0 LTS and 6.3 LTS (Mono and IL2CPP; every platform, WebGL included).

- C# exceptions from `Application.logMessageReceivedThreaded` (any thread), `AppDomain.UnhandledException`
  and unobserved tasks, with Unity's stack as written; `Zharp.CaptureException()` and
  `Zharp.CaptureError()` for handled ones. The same error from the same place at most once a minute.
  `Debug.LogError` and warnings as breadcrumbs, or errors of their own with `logErrors`.
- The build step sets IL2CPP line numbers, and uploads the game's scripts as a source bundle under the
  build's GUID (`zharp.build_id`), for the code that ran. Uploads need `ZHARP_AUTH_TOKEN`; a failed
  upload never fails a build.
- Crashes on the next launch: iOS through the Zharp iOS SDK, which the build step adds to the Xcode
  project with a dSYM upload phase; Windows from Unity's crash folder, uploaded as a minidump; elsewhere
  a run that didn't quit as an `UncleanExit`.
- Hangs: the main thread stuck for 5 s, from a watchdog thread straight to disk (not on WebGL).
- `session.id`, `device.id`, the active scene as `screen.name` (and a `screen` span), `enduser.*`, tags.
- `ZharpWebRequest.Send()`: UnityWebRequest traced with `traceparent`, headers with secrets filtered.
  `Zharp.LoadSceneAsync()` as a `scene.load` span; `Zharp.StartSpan()`.
- An outbox in `persistentDataPath/zharp/`: every batch on disk before it's sent.
- Logs off by default; `logs` or `ZHARP_LOGS=true` sends `Debug.Log` lines.
- Project Settings › Zharp, saved in `Assets/Zharp/Resources/ZharpSettings.json`.

Known limits: Android, macOS and Linux native crashes are `UncleanExit`s until the Android SDK's NDK
support and Zharp's native SDK land; Windows minidump frames need the game's symbols uploaded.
