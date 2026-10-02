#!/usr/bin/env bash
set -euo pipefail

dotnet build src/Echelon.Signal.Host.Wasm/Echelon.Signal.Host.Wasm.fsproj --nologo
dotnet run --project tests/Echelon.Signal.Tests/Echelon.Signal.Tests.fsproj --configuration Release
npx --yes @echelon-foundry/typescript-wasm-kernel@0.6.2 verify --strict
./ros validate
