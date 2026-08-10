#!/usr/bin/env bash
# Serves ./content over the LAN so the headset can fetch Jigs without a rebuild,
# the authoring editor at /editor/, and the web viewer at /viewer/. Writes are
# refused from anywhere but this machine - see tools/serve.py.
set -euo pipefail
PORT="${1:-8000}"
IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo "127.0.0.1")
echo "Manifest URL for the app:"
echo "  http://${IP}:${PORT}/index.json"
echo
echo "Web viewer (share this on the LAN):"
echo "  http://${IP}:${PORT}/viewer/"
echo
echo "Editor (this machine only - saving is refused from anywhere else):"
echo "  http://127.0.0.1:${PORT}/editor/"
echo
exec python3 "$(dirname "$0")/tools/serve.py" "$PORT"
