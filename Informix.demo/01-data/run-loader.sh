#!/usr/bin/env bash
# Builds and runs the demo database loader; every argument is passed on to it
# (--recreate, --dry-run, --no-bson, ...; see loader/Options.cs).
#
# The connection string comes from Informix.demo/.env (INFORMIX_DEMO_CONNECTION; copy .env.example)
# or from an environment variable of that name; the loader reads the file itself.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run --project "$here/loader" -c Release -- "$@"
