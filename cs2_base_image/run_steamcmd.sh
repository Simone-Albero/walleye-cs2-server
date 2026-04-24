#!/bin/bash
set -euo pipefail

# Run steamcmd to install/update CS2 server
./steamcmd.sh +login anonymous +app_update 730 +quit

# Ensure the shared steamcmd volume exists and write a completion marker
mkdir -p /home/steam/steamcmd
touch /home/steam/steamcmd/.cs2_base_done
echo "cs2-base: steamcmd finished, marker created at /home/steam/steamcmd/.cs2_base_done"
