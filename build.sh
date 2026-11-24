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
  /^        \/\/ PB BODY END$/ { exit }
  in_body { sub(/^        /, ""); print }
  /^        \/\/ PB BODY START$/ { in_body = 1 }
' apps/fcs/Program.cs > "$output_dir/apps/fcs/fcs/FlightControl.pb.cs"
test -s "$output_dir/apps/fcs/fcs/FlightControl.pb.cs"

dotnet test TaskBus.Tests/TaskBus.Tests.csproj -c Release --no-build \
  --output "$output_dir/TaskBus.Tests/TaskBus.Tests" \
  --results-directory "$PWD/build/test-results" --logger 'trx;LogFileName=TaskBus.Tests.trx'
