#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
output_dir="$PWD/build/output"
projects=(
  "libs/igc-core/igc-core.csproj"
  "libs/taskbus-consumer/taskbus-consumer.csproj"
  "libs/taskbus-producer/taskbus-producer.csproj"
  "services/broker/broker.csproj"
  "services/consumer/consumer.csproj"
  "services/producer/producer.csproj"
  "apps/amn/amn.csproj"
  "apps/fcs/fcs.csproj"
  "apps/ship-link/ship-link.csproj"
  "apps/sample/sample.csproj"
  "TaskBus.Tests/TaskBus.Tests.csproj"
)

for project in "${projects[@]}"; do
  printf '\n--- Building %s ---\n' "$project"
  dotnet build "$project" -c Release --nologo --output "$output_dir/${project%.csproj}"
done

awk '
  { sub(/\r$/, "") }
  /^        \/\/ PB BODY END$/ { exit }
  in_body { sub(/^        /, ""); print }
  /^        \/\/ PB BODY START$/ { in_body = 1 }
' apps/fcs/Program.cs > "$output_dir/apps/fcs/fcs/FlightControl.pb.cs"
test -s "$output_dir/apps/fcs/fcs/FlightControl.pb.cs"

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

  local library
  for library in libs/igc-core/TaskBusProtocol.cs libs/igc-core/TaskBusIgcHelper.cs "$@"; do
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
}

producer=libs/taskbus-producer/TaskBusProducer.cs
package_taskbus_pb services/broker TaskBroker "$producer"
package_taskbus_pb services/consumer TaskConsumer libs/taskbus-consumer/TaskBusConsumer.cs
package_taskbus_pb services/producer TaskProducer "$producer"
package_taskbus_pb apps/amn AMN.Base "$producer"
package_taskbus_pb apps/sample SampleMain libs/igc-core/Autopilot.cs libs/igc-core/Logger.cs
package_taskbus_pb apps/ship-link ShipLink "$producer"

dotnet test TaskBus.Tests/TaskBus.Tests.csproj -c Release --no-build \
  --output "$output_dir/TaskBus.Tests/TaskBus.Tests" \
  --results-directory "$PWD/build/test-results" --logger 'trx;LogFileName=TaskBus.Tests.trx'
