#!/usr/bin/env bash
# Packs the AgentOrchestrator.CodeQuality analysis-core project into a local
# NuGet feed folder at the repository root, so spikes/agent-pipeline-step can
# consume it as a real package instead of a ProjectReference. Mirrors how an
# external repository (e.g. Agent Studio) would point its own nuget.config at
# a published feed - this just uses a folder feed for the proof.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

FEED_DIR="${1:-.nuget-local-feed}"
rm -rf "$FEED_DIR"
mkdir -p "$FEED_DIR"

dotnet pack src/AgentOrchestrator.CodeQuality/AgentOrchestrator.CodeQuality.csproj \
  --configuration Release \
  --output "$FEED_DIR"

echo "Local feed populated at $FEED_DIR:"
ls -1 "$FEED_DIR"
