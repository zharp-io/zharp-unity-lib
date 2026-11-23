"""A stand-in for Zharp, for the e2e runs. Appends to <out>/received.jsonl one line per request:
  OTLP:      {"path", "auth", "gzip", "body"}                    (POST /api/v1/otlp/v1/logs|traces)
  artifacts: {"path", "auth", "fields", "files": {name: size}}   (POST /api/v1/applications/<id>/artifacts)
  minidump:  {"path", "query", "fields", "dump": size, "mdmp"}   (POST /api/v1/minidump?key=…)
The game's own requests (GET /game/…) go to requests.jsonl with their headers; GET /e2e/done?mode=x
appends x to done.txt. Answers CORS preflights and serves <static> (the WebGL build) on the same port.
Usage: receiver.py <out> [port] [static]"""
import gzip
import http.server
import json
import os
import re
import sys
import urllib.parse

OUT = sys.argv[1] if len(sys.argv) > 1 else "out"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 4318
STATIC = sys.argv[3] if len(sys.argv) > 3 else None
os.makedirs(OUT, exist_ok=True)


def append(name, entry):
    with open(os.path.join(OUT, name), "a") as f:
        f.write(json.dumps(entry) + "\n")


def multipart(body, content_type):
    boundary = re.search(r'boundary="?([^";]+)"?', content_type).group(1).encode()
    fields, files = {}, {}
    for part in body.split(b"--" + boundary):
        part = part.strip(b"\r\n")
        if not part or part == b"--" or b"\r\n\r\n" not in part:
            continue
        head, data = part.split(b"\r\n\r\n", 1)
        disposition = head.decode("utf-8", "replace")
        # Quoted as RFC 7578 has it, or bare as Mono's HttpClient writes it.
        name = re.search(r'\bname="?([^";\r\n]*)"?', disposition).group(1)
        if 'filename=' in disposition:
            files[name] = data
        else:
            fields[name] = data.decode("utf-8", "replace")
    return fields, files


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=STATIC or OUT, **kwargs)

    def end_headers(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Allow-Methods", "POST, GET, OPTIONS")
        self.send_header("Cross-Origin-Resource-Policy", "cross-origin")
        super().end_headers()

    def do_OPTIONS(self):
        self.send_response(204)
        self.end_headers()

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        if url.path == "/e2e/done":
            mode = urllib.parse.parse_qs(url.query).get("mode", [""])[0]
            with open(os.path.join(OUT, "done.txt"), "a") as f:
                f.write(mode + "\n")
            return self.reply(200, b"ok")
        if url.path.startswith("/game/"):
            append("requests.jsonl", {"path": self.path, "headers": dict(self.headers)})
            return self.reply(200, b'{"scores": []}')
        super().do_GET()

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        url = urllib.parse.urlparse(self.path)
        gz = self.headers.get("Content-Encoding") == "gzip"
        if gz:
            body = gzip.decompress(body)
        ctype = self.headers.get("Content-Type", "")
        if url.path.endswith("/artifacts"):
            fields, files = multipart(body, ctype)
            append("received.jsonl", {"path": url.path, "auth": self.headers.get("Authorization", ""), "fields": fields,
                                      "files": {k: len(v) for k, v in files.items()},
                                      "bundle": json.loads(gzip.decompress(files["file"])) if fields.get("kind") == "source-bundle" else None})
            return self.reply(201, b'{"ok": true}')
        if url.path.endswith("/minidump"):
            fields, files = multipart(body, ctype)
            dump = files.get("upload_file_minidump", b"")
            append("received.jsonl", {"path": url.path, "query": urllib.parse.parse_qs(url.query), "fields": fields,
                                      "dump": len(dump), "mdmp": dump[:4] == b"MDMP"})
            return self.reply(200, b"crash-1")
        entry = {"path": url.path, "auth": self.headers.get("Authorization", ""), "gzip": gz, "body": json.loads(body or b"{}")}
        append("received.jsonl", entry)
        print("POST", url.path, len(body), flush=True)
        self.reply(200, b"{}")

    def reply(self, status, data):
        self.send_response(status)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
