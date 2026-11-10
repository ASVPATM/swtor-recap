#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${PROJECT_FLOW_DATA:?Set PROJECT_FLOW_DATA to an isolated preview directory}"
export SWTOR_DOTNET_CLI_HOME="$PROJECT_FLOW_DATA/dotnet-home"
export SWTOR_NUGET_PACKAGES="${SWTOR_NUGET_PACKAGES:-$PROJECT_FLOW_DATA/nuget}"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
bash scripts/dotnet.sh restore src/SwtorRecap.Desktop/SwtorRecap.Desktop.csproj --locked-mode --disable-parallel -m:1
bash scripts/dotnet.sh build src/SwtorRecap.Desktop -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
exec bash scripts/dotnet.sh src/SwtorRecap.Desktop/bin/Release/net10.0/SwtorRecap.dll --preview --fixture tests/Fixtures/dashboard-year.json
