#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_HOME="${SWTOR_DOTNET_CLI_HOME:-$ROOT/.artifacts/dotnet-home}"
export NUGET_PACKAGES="${SWTOR_NUGET_PACKAGES:-$ROOT/.artifacts/nuget}"
if command -v dotnet >/dev/null 2>&1; then exec dotnet "$@"; fi
if [[ -x /private/tmp/swtor-dotnet/dotnet ]]; then exec /private/tmp/swtor-dotnet/dotnet "$@"; fi
printf '%s\n' 'Install the .NET SDK specified in global.json, then rerun this command.' >&2
exit 127
