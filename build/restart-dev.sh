#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
compose_dev up -d --force-recreate jellyfin
