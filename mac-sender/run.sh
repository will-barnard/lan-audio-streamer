#!/usr/bin/env bash
# Turn the macOS sender ON. Loads .env from this directory and starts the daemon.
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "No .env found. Copy .env.example to .env and edit it first."
  exit 1
fi

echo "Building (first run may take a moment)..."
swift build -c release

echo "Starting LAN Audio Bridge sender. Press Ctrl-C to stop."
# `.env` is read by the app from the current working directory.
exec swift run -c release LANAudioSender "$@"
