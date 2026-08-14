#!/usr/bin/env bash
# Builds the hosted image. Run inside WSL (Ubuntu-24.04) — there is no Docker on the Windows side,
# and the box itself has 2 GB of RAM against the SDK build stage's appetite.
#
# --provenance/--sbom off because buildx otherwise writes an attestation manifest list, and a
# manifest list is awkward to move with the docker save | ssh | docker load that ships this.
set -euo pipefail
cd "/mnt/c/Users/nsquires/source/repos/ING eBay AutoLister"
docker build --provenance=false --sbom=false -t ing-listing-engine:latest .
docker image inspect ing-listing-engine:latest --format '{{.Id}} {{.Created}} {{.Os}}/{{.Architecture}}'
