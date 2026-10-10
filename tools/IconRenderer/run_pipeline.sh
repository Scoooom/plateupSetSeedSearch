#!/usr/bin/env bash
# Full appliance-icon render pipeline - run this in tmux and let it finish, then report back.
#
# Steps:
#   1. Run the C# extractor (ApplianceIconManifestExtractor + ApplianceModelExtractor) to produce
#      data/settings/shop/appliance-icon-manifest.json AND one combined OBJ+MTL 3D model per
#      appliance, parsed directly from the mesh/transform/material YAML already sitting in
#      PlateUpSourceFiles/AssetRipper/ExportedProject/ - no AssetRipper re-run needed. Verified this
#      session against the real asset dump: 395/428 appliances got a real model (the rest have no
#      visible geometry in the source data at all - logic/trigger/decorator entities, not a parsing
#      gap), bounding boxes sanity-checked (~1m appliance-scale, sitting near floor level).
#   2. Install Blender (apt) and batch-render every model to a square transparent PNG via
#      tools/IconRenderer/render_icons.py.
#
# Both steps are now straightforward - the one earlier, genuinely uncertain plan (re-running
# AssetRipper live to export FBX) turned out to be a dead end (that edition has no mesh export
# format at all) and has been dropped entirely in favor of parsing the existing static YAML dump
# directly. The one part NOT verified in the environment this was written in is the actual Blender
# render (no Blender available there) - if step 2 produces obviously wrong-looking icons, that's
# the first place to look (camera framing/lighting in render_icons.py), not step 1.
#
# Usage:
#   cd /opt/plateupSetSeedProject/plateupSetSeedSearch
#   bash tools/IconRenderer/run_pipeline.sh 2>&1 | tee /tmp/icon-pipeline.log

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_FILES="/opt/plateupSetSeedProject/PlateUpSourceFiles"
DATA_SETTINGS_DIR="$REPO_ROOT/data/settings"
MODELS_DIR="/tmp/appliance-models"
ICON_OUTPUT_DIR="$REPO_ROOT/src/PlateUpSeedTools.Web/wwwroot/images/appliances"

echo "=== Step 1/2: Extract icon manifest + OBJ/MTL models (C#, via Docker) ==="
echo "(this regenerates the whole data/settings/ tree, same as any normal DataExtractor run - safe"
echo "and idempotent, matches what's already committed, just also now includes the icon manifest)"
mkdir -p "$MODELS_DIR"
docker run --rm \
    -v "$REPO_ROOT":/src \
    -v "$SOURCE_FILES":/sourcefiles:ro \
    -v "$DATA_SETTINGS_DIR":/out \
    -v "$MODELS_DIR":/models \
    -w /src/tools/PlateUpSeedTools.DataExtractor \
    mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet run -- /sourcefiles /out /models

echo
echo "=== Step 2/2: Batch-render icons with Blender ==="
if ! command -v blender >/dev/null 2>&1; then
    echo "Installing Blender..."
    apt-get update -qq
    apt-get install -y -qq blender
fi

mkdir -p "$ICON_OUTPUT_DIR"
blender --background --python "$REPO_ROOT/tools/IconRenderer/render_icons.py" -- \
    "$MODELS_DIR" \
    "$ICON_OUTPUT_DIR"

echo
echo "=== Pipeline complete ==="
echo "Rendered icons are in: $ICON_OUTPUT_DIR"
echo "Tell Claude it's done - next step is spot-checking a sample, replacing the 5 old fan-sourced"
echo "icons for visual consistency, verifying in the app, and updating docs/SOURCE.md."
