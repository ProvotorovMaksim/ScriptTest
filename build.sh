#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
output_dir="$PWD/build/output"
projects=(
  "libs/igc-core/igc-core.csproj"
  "libs/taskbus-consumer/taskbus-consumer.csproj"
  "libs/taskbus-producer/taskbus-producer.csproj"
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
  "TaskBus.Tests/TaskBus.Tests.csproj"
)

for project in "${projects[@]}"; do
  printf '\n--- Building %s ---\n' "$project"
  dotnet build "$project" -c Release --nologo --output "$output_dir/${project%.csproj}"
done

pb_check_dir="$PWD/build/pb-check"
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

awk '
  { sub(/\r$/, "") }
  /^[[:space:]]*\/\/ PB BODY END$/ { in_body = 0; next }
  in_body { sub(/^    /, ""); print }
  /^[[:space:]]*\/\/ PB BODY START$/ { in_body = 1 }
' apps/fcs/Program.cs apps/fcs/DockLease.cs > "$output_dir/apps/fcs/fcs/FlightControl.pb.cs"
wrap_pb_for_compile_check "$output_dir/apps/fcs/fcs/FlightControl.pb.cs" fcs

package_taskbus_pb() {
  local project="$1"
  local name="$2"
  shift 2
  local destination="$output_dir/$project/${project##*/}/$name.pb.cs"
  awk '
    { sub(/\r$/, "") }
    /^[[:space:]]*#region PreludeFooter/ { footer = 1; exit }
    in_body { sub(/^        /, ""); print }
    /^[[:space:]]+#endregion[[:space:]]*$/ { in_body = 1 }
    END { if (!in_body || !footer) exit 1 }
  ' "$project/Program.cs" > "$destination"

  if [[ "$project" != apps/radar ]]; then
    set -- libs/igc-core/TaskBusProtocol.cs libs/igc-core/TaskBusIgcHelper.cs "$@"
  fi
  local library
  for library in "$@"; do
    printf '\n' >> "$destination"
    awk '
      { sub(/\r$/, "") }
      /^}$/ { closed = 1; exit }
      in_namespace { sub(/^    /, ""); print }
      /^{$/ { in_namespace = 1 }
      END { if (!in_namespace || !closed) exit 1 }
    ' "$library" >> "$destination"
  done

  awk '{ gsub(/CultureInfo\.InvariantCulture/, "System.Globalization.CultureInfo.InvariantCulture"); gsub(/NumberStyles\./, "System.Globalization.NumberStyles."); print }' \
    "$destination" > "$destination.tmp"
  mv "$destination.tmp" "$destination"
  local namespace="${name//./_}"
  wrap_pb_for_compile_check "$destination" "$namespace"
}

producer=libs/taskbus-producer/TaskBusProducer.cs
package_taskbus_pb services/broker TaskBroker "$producer"
package_taskbus_pb services/galnet GalnetRelay libs/igc-core/GalnetTopology.cs
package_taskbus_pb services/consumer TaskConsumer libs/taskbus-consumer/TaskBusConsumer.cs libs/taskbus-consumer/GalnetWorkerBridge.cs libs/taskbus-consumer/MiningRouteLoader.cs
package_taskbus_pb services/producer TaskProducer "$producer"
package_taskbus_pb apps/amn AMN.Base "$producer" libs/taskbus-consumer/GalnetWorkerBridge.cs
package_taskbus_pb apps/platform AmnPlatform libs/taskbus-consumer/TaskBusConsumer.cs libs/taskbus-consumer/GalnetWorkerBridge.cs libs/taskbus-consumer/MiningRouteLoader.cs
package_taskbus_pb apps/sample SampleMain libs/igc-core/Autopilot.cs libs/igc-core/Logger.cs
package_taskbus_pb apps/ship-link ShipLink

package_taskbus_pb apps/radar Radar libs/radar-core/*.cs

dotnet build tests/PbCompile/PbCompile.csproj -c Release --nologo

dotnet test TaskBus.Tests/TaskBus.Tests.csproj -c Release --no-build \
  --output "$output_dir/TaskBus.Tests/TaskBus.Tests" \
  --results-directory "$PWD/build/test-results" --logger 'trx;LogFileName=TaskBus.Tests.trx'
