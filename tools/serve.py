#!/usr/bin/env python3
"""Dev server for JigClone content.

GET  serves ./content on 0.0.0.0 so the headset can fetch it, and the authoring
     editor at /editor/.
PUT  writes into ./content so the editor can save without a file shuffle.

Reads are open to the LAN because the headset needs them. Writes are not: a PUT
from anything other than loopback is refused, or every device on the wifi can
overwrite the content you just authored.
"""

import http.server
import os
import socketserver
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONTENT = (ROOT / "content").resolve()
EDITOR = (ROOT / "tools" / "editor").resolve()

WRITABLE_SUFFIXES = {".json", ".glb"}
MAX_UPLOAD = 256 * 1024 * 1024
LOOPBACK = {"127.0.0.1", "::1"}


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(CONTENT), **kwargs)

    # -- GET ---------------------------------------------------------------

    def translate_path(self, path):
        """Content at /, the editor at /editor/. Two trees, one origin, so the
        editor's PUT is same-origin and there is no CORS to configure."""
        url = path.split("?", 1)[0].split("#", 1)[0]
        if url == "/editor" or url.startswith("/editor/"):
            saved = self.directory
            self.directory = str(EDITOR)
            try:
                return super().translate_path(path[len("/editor") :] or "/")
            finally:
                self.directory = saved
        return super().translate_path(path)

    def end_headers(self):
        # The whole point of the slice is that editing content changes the app.
        # A 304 from the editor's own fetch would hide the edit you just made.
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    # -- PUT ---------------------------------------------------------------

    def do_PUT(self):
        if self.client_address[0] not in LOOPBACK:
            self.reject(403, f"writes are loopback-only; you are {self.client_address[0]}")
            return

        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            self.reject(411, "PUT needs a Content-Length")
            return
        if length > MAX_UPLOAD:
            self.reject(413, f"{length} bytes exceeds the {MAX_UPLOAD} byte limit")
            return

        target = self.writable_target(self.path)
        if target is None:
            self.reject(403, f"{self.path} is not a writable content path")
            return

        body = self.rfile.read(length)
        if len(body) != length:
            self.reject(400, "body shorter than Content-Length")
            return

        # Write beside the target then rename. A half-written scene.json would
        # otherwise destroy authored content on a dropped connection, and
        # os.replace is atomic within a filesystem.
        target.parent.mkdir(parents=True, exist_ok=True)
        tmp = target.with_name(target.name + ".tmp")
        try:
            tmp.write_bytes(body)
            os.replace(tmp, target)
        except OSError as e:
            tmp.unlink(missing_ok=True)
            self.reject(500, f"could not write {target.name}: {e}")
            return

        self.send_response(204)
        self.end_headers()
        print(f"  wrote {target.relative_to(CONTENT)} ({length} bytes)", flush=True)

    def writable_target(self, urlpath):
        """The file a PUT may write, or None. Never returns a path outside content/."""
        url = urlpath.split("?", 1)[0].split("#", 1)[0]

        # Refuse traversal rather than normalise it. SimpleHTTPRequestHandler
        # silently drops '..' components, which would turn a caller's buggy
        # '/../scene.json' into a successful write to a different file than it
        # asked for - the write reports 204 and the bug stays invisible.
        if any(part == ".." for part in url.split("/")):
            return None

        # resolve() plus relative_to is what catches a symlink pointing out of the
        # tree, which a component filter cannot see. Called unbound so the
        # /editor/ mapping above cannot be used to write into the tools tree.
        raw = http.server.SimpleHTTPRequestHandler.translate_path(self, urlpath)
        target = Path(raw).resolve()

        try:
            target.relative_to(CONTENT)
        except ValueError:
            return None

        if target == CONTENT or target.suffix.lower() not in WRITABLE_SUFFIXES:
            return None
        return target

    def reject(self, code, message):
        print(f"  refused PUT {self.path}: {message}", flush=True)
        self.send_error(code, message)


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    # Threaded: the headset pulling a 7MB glb must not block the editor saving.
    with http.server.ThreadingHTTPServer(("0.0.0.0", port), Handler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
