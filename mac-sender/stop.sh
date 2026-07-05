#!/usr/bin/env bash
# Turn the macOS sender OFF.
set -uo pipefail
pkill -f "LANAudioSender" && echo "Sender stopped." || echo "Sender was not running."
