#!/usr/bin/env python3
"""Self-check for the serve.py write guards.

Run: python3 tools/test_serve.py

These three guards are the only thing standing between an authoring convenience
and "anything on the wifi can overwrite your content", so they get a test that
can actually fail. Exits non-zero on failure.
"""

import http.server
import socket
import sys
import threading
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import serve  # noqa: E402

PROBE = serve.CONTENT / "__selftest__.json"
ESCAPE = serve.ROOT / "__escape__.json"
ESCAPE_NORMALISED = serve.CONTENT / "__escape__.json"


def put(url, body=b"{}"):
    """Returns the HTTP status, treating an error response as a status not a raise."""
    req = urllib.request.Request(url, data=body, method="PUT")
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


def lan_ip():
    """This machine's LAN address, or None. Connecting to it from this same machine
    produces a non-loopback client_address, which is what the guard keys on - so this
    is a real remote-write test, not a mock."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("192.0.2.1", 9))  # TEST-NET-1, never routed; no packet is sent
        ip = s.getsockname()[0]
        return None if ip.startswith("127.") else ip
    except OSError:
        return None
    finally:
        s.close()


def main():
    class Quiet(serve.Handler):
        def log_message(self, *a):
            pass

    httpd = http.server.ThreadingHTTPServer(("0.0.0.0", 0), Quiet)
    port = httpd.server_address[1]
    threading.Thread(target=httpd.serve_forever, daemon=True).start()

    local = f"http://127.0.0.1:{port}"
    failures = []

    def check(name, got, want):
        if got == want:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name}: got {got}, want {want}")
            failures.append(name)

    try:
        PROBE.unlink(missing_ok=True)
        ESCAPE.unlink(missing_ok=True)

        check("localhost PUT of .json is accepted", put(f"{local}/__selftest__.json"), 204)
        check("...and the bytes landed", PROBE.read_bytes() if PROBE.exists() else None, b"{}")

        check("path traversal is refused", put(f"{local}/../__escape__.json"), 403)
        check("...and nothing was written above content/", ESCAPE.exists(), False)
        check("...nor normalised into content/", ESCAPE_NORMALISED.exists(), False)

        check("a non-content suffix is refused", put(f"{local}/__selftest__.txt"), 403)
        check("a directory target is refused", put(f"{local}/"), 403)
        check("an empty body is refused", put(f"{local}/__selftest__.json", b""), 411)

        ip = lan_ip()
        if ip:
            PROBE.write_bytes(b'{"sentinel":1}')
            check(f"PUT from the LAN address {ip} is refused",
                  put(f"http://{ip}:{port}/__selftest__.json"), 403)
            check("...and the file is untouched", PROBE.read_bytes(), b'{"sentinel":1}')
        else:
            print("  SKIP  LAN-write test: no non-loopback address on this machine")

        with urllib.request.urlopen(f"{local}/index.json", timeout=5) as r:
            check("GET still serves content", r.status, 200)

    finally:
        PROBE.unlink(missing_ok=True)
        ESCAPE.unlink(missing_ok=True)
        ESCAPE_NORMALISED.unlink(missing_ok=True)
        httpd.shutdown()

    if failures:
        print(f"\n{len(failures)} failure(s): {', '.join(failures)}")
        return 1
    print("\nall guards hold")
    return 0


if __name__ == "__main__":
    sys.exit(main())
