#!/usr/bin/env bash
# Serves ./content over the LAN so the headset can fetch Jigs without a rebuild.
set -euo pipefail
PORT="${1:-8000}"
IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo "127.0.0.1")
echo "Manifest URL for the app:"
echo "  http://${IP}:${PORT}/index.json"
echo
cd "$(dirname "$0")/content"
python3 -m http.server "$PORT" --bind 0.0.0.0
