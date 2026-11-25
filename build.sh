#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
output_dir="$PWD/build/output"
pb_projects=(
  "services/broker/broker.csproj"
  "services/galnet/galnet.csproj"
  "services/consumer/consumer.csproj"
  "services/producer/producer.csproj"
  "apps/amn/amn.csproj"
  "apps/platform/platform.csproj"
  "apps/fcs/fcs.csproj"
  "apps/ship-link/ship-link.csproj"
  "apps/sample/sample.csproj"
  "apps/radar/radar.csproj"
)

release_dir="$PWD/build/mdk-scripts"
mkdir -p "$release_dir"
mdk_output="$release_dir"
if command -v cygpath >/dev/null; then
  mdk_output="$(cygpath -am "$release_dir")"
fi
export MdkArguments="-output \"$mdk_output\" ${MdkArguments:-}"
for project in "${pb_projects[@]}"; do
  name="${project##*/}"
  rm -f "$release_dir/${name%.csproj}/script.cs"
done

for project in libs/igc-core/igc-core.csproj libs/taskbus-consumer/taskbus-consumer.csproj \
  libs/taskbus-producer/taskbus-producer.csproj "${pb_projects[@]}" TaskBus.Tests/TaskBus.Tests.csproj; do
  printf '\n--- Building %s ---\n' "$project"
  dotnet build "$project" -c Release --nologo --output "$output_dir/${project%.csproj}"
done

pb_check_inputs="../../build/pb-check/mdk"
pb_check_dir="$PWD/tests/PbCompile/$pb_check_inputs"
mkdir -p "$pb_check_dir"

wrap_pb_for_compile_check() {
  local body="$1"
  local namespace="$2"
  test -s "$body"
  {
    cat <<'USINGS'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRageMath;
USINGS
    printf 'namespace %s { public sealed class Program : MyGridProgram {\n' "$namespace"
    cat "$body"
    printf '\n}}\n'
  } > "$pb_check_dir/$namespace.cs"
}

for project in "${pb_projects[@]}"; do
  name="${project##*/}"
  name="${name%.csproj}"
  body="$release_dir/$name/script.cs"
  if [[ ! -s "$body" ]]; then
    printf 'Missing or empty MDK script: %s\n' "$body" >&2
    exit 1
  fi
  characters="$(wc -m < "$body")"
  if (( characters > 100000 )); then
    printf 'MDK script exceeds the 100000-character PB limit: %s (%d characters)\n' "$body" "$characters" >&2
    exit 1
  fi
  wrap_pb_for_compile_check "$body" "${name//-/_}"
  printf 'Validated MDK input: %s (%d characters)\n' "$body" "$characters"
done

dotnet build tests/PbCompile/PbCompile.csproj -c Release --nologo -p:PbCheckInputs="$pb_check_inputs"

dotnet test TaskBus.Tests/TaskBus.Tests.csproj -c Release --no-build \
  --output "$output_dir/TaskBus.Tests/TaskBus.Tests" \
  --results-directory "$PWD/build/test-results" --logger 'trx;LogFileName=TaskBus.Tests.trx'
