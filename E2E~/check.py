"""Checks what the stand-in receiver got for one e2e scenario.
Usage: check.py <scenario> <received.jsonl> [requests.jsonl] [build-guid.txt]
Scenarios: error, logs, hang, crash (Linux/Android/macOS: an UncleanExit), minidump (Windows),
managed-crash, upload (the build step's source bundle), web (WebGL), ios-crash."""
import json
import os
import sys

scenario, received_path = sys.argv[1], sys.argv[2]
requests_path = sys.argv[3] if len(sys.argv) > 3 else ""
guid_path = sys.argv[4] if len(sys.argv) > 4 else ""


def values(attributes):
    return {a["key"]: list(a["value"].values())[0] for a in attributes}


entries = [json.loads(line) for line in open(received_path)] if os.path.exists(received_path) else []
logs, spans = [], []
for entry in entries:
    body = entry.get("body") or {}
    for rl in body.get("resourceLogs", []):
        resource = values(rl["resource"]["attributes"])
        for scope in rl["scopeLogs"]:
            for r in scope["logRecords"]:
                logs.append({"resource": resource, "severity": r["severityText"], "body": r["body"]["stringValue"], "a": values(r["attributes"])})
    for rs in body.get("resourceSpans", []):
        resource = values(rs["resource"]["attributes"])
        for scope in rs["scopeSpans"]:
            for s in scope["spans"]:
                spans.append({"resource": resource, "name": s["name"], "kind": s["kind"], "trace": s["traceId"], "id": s["spanId"], "a": values(s["attributes"])})
requests = [json.loads(line) for line in open(requests_path)] if requests_path and os.path.exists(requests_path) else []
guid = open(guid_path).read().strip() if guid_path and os.path.exists(guid_path) else ""
errors = [l for l in logs if "exception.type" in l["a"]]
problems = []


def expect(condition, what):
    if not condition:
        problems.append(what)


def of_type(t):
    return [e for e in errors if e["a"]["exception.type"] == t]


def common(error_list):
    for e in error_list:
        r = e["resource"]
        expect(r.get("service.name") == "shop-quest", "service.name: the product name as Zharp names its app")
        expect(r.get("service.version") == "1.4.0", "service.version is the game's version")
        expect(r.get("telemetry.sdk.language") == "unity", "telemetry.sdk.language")
        expect(len(r.get("device.id", "")) == 32, "device.id")
        expect(len(e["a"].get("session.id", "")) == 32, "session.id")
        expect(e["a"].get("enduser.id") == "player-42", "enduser.id on " + e["body"])
        expect(e["a"].get("screen.name") == "Level", "screen.name = the scene, on " + e["body"])
        env = os.environ.get("ZHARP_ENVIRONMENT")
        if env:
            expect(r.get("deployment.environment.name") == env, "deployment.environment.name = ZHARP_ENVIRONMENT (%s): %s" % (env, r.get("deployment.environment.name")))
        if guid:
            expect(e["a"].get("zharp.build_id") == guid, "zharp.build_id = the build's GUID (%s): %s" % (guid, e["a"].get("zharp.build_id")))
    for entry in entries:
        if "body" in entry:
            expect(entry["auth"] == "Bearer zpk_e2e_test", "the key as a bearer token")


if scenario in ("error", "web"):
    nre = of_type("NullReferenceException")
    expect(len(nre) == 1, "one NullReferenceException (Update threw once): got %d" % len(nre))
    for e in nre:
        stack = e["a"]["exception.stacktrace"].split("\n")
        # Mono's JIT inlines AddToCart into Open (the frame is then Open's call to it, Checkout.cs:19);
        # a Mono release player has no line numbers at all.
        mono = e["resource"].get("unity.scripting_backend") == "mono"
        mono_release = mono and e["resource"].get("unity.build") == "release"
        top = ("Shop.Checkout.AddToCart", "Shop.Checkout.Open") if mono else ("Shop.Checkout.AddToCart",)
        expect(any(t in line for t in top for line in stack[1:2]), "the top frame is AddToCart: %s" % stack[:3])
        if scenario == "error" and not mono_release:
            lines = ("Checkout.cs:24", "Checkout.cs:19") if mono else ("Checkout.cs:24",)
            expect(any(n in line for n in lines for line in stack[1:2]), "line numbers: Checkout.cs:24 in %s" % stack[:3])
        expect(e["a"]["zharp.handled"] == "false", "unhandled")
        expect("Coupon SPRING expired" in e["a"].get("zharp.breadcrumbs", ""), "the warning as a breadcrumb")
        expect("Inventory out of sync" in e["a"].get("zharp.breadcrumbs", ""), "Debug.LogError as a breadcrumb")
    handled = [e for e in of_type("Error") if e["a"]["zharp.handled"] == "true"]
    expect(len(handled) == 1, "CaptureError once")
    for e in handled:
        expect("Shop.Checkout" in e["a"]["exception.stacktrace"].split("\n")[1], "handled error starts at the game's frame, not Zharp's: %s" % e["a"]["exception.stacktrace"])
        expect(str(e["a"].get("slot")) in ("2", "2.0"), "the error's own attribute")
    caught = [e for e in of_type("ArgumentOutOfRangeException") if e["a"]["zharp.handled"] == "true"]
    expect(len(caught) == 1, "CaptureException once: got %d" % len(caught))
    for e in caught:
        stack = e["a"]["exception.stacktrace"].split("\n")
        expect(any("Shop.Checkout.Charge" in line for line in stack[1:2]), "the caught exception's top frame is Charge: %s" % stack[:3])
        expect(any("Shop.Checkout.Pay" in line for line in stack), "then Pay: %s" % stack[:4])
        expect(str(e["a"].get("coins")) in ("0", "0.0"), "CaptureException's own attribute")
    expect(not [e for e in errors if "Inventory out of sync" in e["body"]], "Debug.LogError is no error while logErrors is off")
    common(nre + handled + caught)
    client = [s for s in spans if s["kind"] == 3]
    expect(len(client) == 1, "one client span for the request")
    for s in client:
        expect(s["a"].get("http.request.header.authorization") == "[Filtered]", "Authorization filtered")
        expect("key=[Filtered]" in s["a"].get("url.full", ""), "the URL's key filtered")
        expect(s["a"].get("http.response.status_code") == "200" or s["a"].get("http.response.status_code") == 200, "status code")
    game = [r for r in requests if r["path"].startswith("/game/")]
    expect(len(game) == 1 and client and {k.lower(): v for k, v in game[0]["headers"].items()}.get("traceparent", "").split("-")[1:3] == [client[0]["trace"], client[0]["id"]],
           "traceparent sent with the request's span")
    expect(any(s["name"] == "screen Level" for s in spans), "a screen span for the scene")
    if scenario == "web":
        expect(all(not e.get("gzip") for e in entries if "body" in e), "WebGL sends uncompressed")
    else:
        expect(all(e.get("gzip") for e in entries if "body" in e), "gzipped")
elif scenario == "logs":
    lines = [l for l in logs if "exception.type" not in l["a"]]
    expect(any(l["body"] == "Shop opened with 3 items" and l["severity"] == "INFO" for l in lines), "Debug.Log as info")
    expect(any(l["body"] == "Low on gold" and l["severity"] == "WARN" for l in lines), "Debug.LogWarning as warn")
elif scenario == "logs-off":
    expect(not [l for l in logs if "exception.type" not in l["a"] and l["body"] == "Shop opened with 3 items"], "no logs unless logs is on")
elif scenario == "hang":
    anr = of_type("ApplicationNotResponding")
    expect(len(anr) == 1, "one hang: got %d" % len(anr))
    for e in anr:
        expect(e["a"]["exception.message"].startswith("The main thread was blocked for"), "hang message")
    common(anr)
elif scenario == "crash":
    unclean = of_type("UncleanExit")
    expect(len(unclean) == 1, "the crashed run as one UncleanExit on the next launch: got %d" % len(unclean))
    for e in unclean:
        expect(e["severity"] == "FATAL", "fatal")
        expect(e["a"].get("zharp.error.kind") == "crash", "a crash")
    common(unclean)
elif scenario == "managed-crash":
    died = [e for e in errors if e["a"]["exception.message"] == "Sync thread died"]
    expect(len(died) >= 1, "the thread's exception")
elif scenario == "minidump":
    dumps = [e for e in entries if e["path"].endswith("/minidump")]
    expect(len(dumps) == 1, "one minidump upload: got %d" % len(dumps))
    for d in dumps:
        expect(d["mdmp"], "a minidump (MDMP)")
        expect(d["query"].get("key") == ["zpk_e2e_test"], "the key in the URL")
        expect(d["fields"].get("zharp.release") == "1.4.0", "release")
        expect(len(d["fields"].get("zharp.session_id", "")) == 32, "the crashed run's session")
        expect(d["fields"].get("zharp.user.id") == "player-42", "the crashed run's player")
        expect(d["fields"].get("screen.name") == "Level", "the crashed run's scene")
        expect("ForceCrash" in d["fields"].get("unity.player_log", ""), "the player log's end, with the crash's stack")
    expect(not of_type("UncleanExit"), "no UncleanExit when the dump was sent")
    expect(not of_type("ApplicationNotResponding"), "no hang for the freeze while the crash handler wrote the dump")
elif scenario == "upload":
    uploads = [e for e in entries if e["path"] == "/api/v1/applications/42/artifacts"]
    expect(len(uploads) == 1, "one source bundle upload: got %d" % len(uploads))
    for u in uploads:
        expect(u["fields"].get("kind") == "source-bundle", "kind")
        expect(u["fields"].get("debugId") == guid, "under the build GUID %s: %s" % (guid, u["fields"].get("debugId")))
        expect("Assets/Scripts/Shop/Checkout.cs" in (u["bundle"] or {}).get("files", {}), "the game's scripts")
        expect(not any("/Editor/" in p for p in (u["bundle"] or {}).get("files", {})), "no Editor scripts")
elif scenario == "ios-crash":
    crashes = [e for e in errors if e["resource"].get("telemetry.sdk.language") == "swift"]
    expect(len(crashes) == 1, "the iOS SDK's crash report on the next launch: got %d" % len(crashes))
    for e in crashes:
        expect(e["a"].get("enduser.id") == "player-42", "the player")
        expect(len(e["a"].get("unity.session.id", "")) == 32, "the Unity session as a tag")
    expect(not of_type("UncleanExit"), "no UncleanExit on iOS (the iOS SDK reports crashes)")
else:
    problems.append("unknown scenario " + scenario)

if problems:
    print("FAIL %s:\n  - %s" % (scenario, "\n  - ".join(problems)))
    print("received: %d errors %s, %d logs, %d spans" % (len(errors), [e["a"]["exception.type"] for e in errors], len(logs), len(spans)))
    sys.exit(1)
print("ok %s: %d errors, %d logs, %d spans" % (scenario, len(errors), len(logs), len(spans)))
