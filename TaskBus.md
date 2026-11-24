# TaskBus for Space Engineers PBs

TaskBus is a Kafka-inspired persistent work queue over Space Engineers IGC. It is not Apache Kafka: the broker is a Programmable Block, IGC is broadcast-based, and persistence is limited to each PB's `Storage`.

## Architecture

The repository follows the **MDK² monorepo** pattern:

```
libs/igc-core/        # Shared mixin library (IGC transport, JSON protocol, Storage helpers)
services/broker/      # TaskBroker PB — domain-neutral task queue broker
services/consumer/    # TaskConsumer PB — domain-neutral task consumer with workflow checkpoints
apps/amn/             # AMN.Base PB — resource deficit producer (mining fleet)
apps/sample/          # SampleMain PB — simple PING/PONG example
tests/                # Unit tests for TaskBusProtocol
```

### Layers

| Layer | Purpose | Key Files |
|-------|---------|-----------|
| `libs/igc-core` | Shared protocol and IGC helpers, plus linked PB utilities | `TaskBusProtocol.cs`, `TaskBusIgcHelper.cs`, `Logger.cs`, `Autopilot.cs` |
| `services/broker` | Queue broker: storage, deduplication, leases, consumer groups | `Program.cs` |
| `services/consumer` | Polling consumer: checkpoints, stage workflow, commit ACK | `Program.cs` |
| `apps/amn` | Resource monitor/producer: scans inventory, publishes deficits | `Program.cs` |
| `apps/sample` | Minimal PING/PONG PB | `Program.cs` |

## Build and Install

### Unified build (recommended)

```sh
export SPACE_ENGINEERS_BIN64="/path/to/DedicatedServer64"
./build.sh
```

Use the .NET 8 SDK from `global.json` and the Windows Dedicated Server libraries from Steam app `298740`. The script builds the six PB projects, three libraries, and test project into separate directories under `build/output/`, then runs the tests. The DLLs are compile outputs. In-game deployment requires packaging the PB source with its shared code for the Programmable Block API.

### Per-project build

```sh
dotnet build services/broker/broker.csproj
dotnet build services/consumer/consumer.csproj
dotnet build apps/amn/amn.csproj
dotnet build apps/sample/sample.csproj
```

### Unit tests

```sh
dotnet test TaskBus.Tests/TaskBus.Tests.csproj
```

## Roles

- **libs/igc-core/TaskBusProtocol.cs** — shared versioned JSON envelope and JSON helpers (public, XML-documented).
- **libs/taskbus-producer/TaskBusProducer.cs** — reusable transport-facing producer API (public, XML-documented).
- **libs/igc-core/TaskBusIgcHelper.cs** — shared IGC/Storage helper: envelope parsing, JSON property extraction, Base64 storage encoding/decoding. Eliminates duplicated boilerplate across all PB programs.
- **libs/igc-core/Logger.cs** — simple terminal-block logger (public, XML-documented).
- **libs/igc-core/Autopilot.cs** — remote control autopilot helper with null-safe constructors (public, XML-documented).
- **services/broker/Program.cs** — domain-neutral broker: stores arbitrary payloads, deduplicates request IDs per producer PB, assigns tasks by consumer group, uses leases/epochs, and persists the queue.
- **services/consumer/Program.cs** — domain-neutral consumer: polls one group, persists assignments and workflow checkpoints, and commits only after all payload-provided steps are acknowledged.
- **apps/amn/Program.cs** — AMN-specific producer: computes resource deficits across cargo containers, assemblers, refineries, cockpits, and remote controls; builds request payloads; publishes with persistent request IDs.
- **apps/sample/Program.cs** — minimal PING/PONG example PB.

## Delivery Guarantee

The queue provides at-least-once assignment, producer-side request deduplication, and consumer-side durable checkpoints. It cannot guarantee exactly-once physical work: if a PB loses power after performing a world action but before persisting its checkpoint, the action may be attempted again. Physical handlers must therefore verify world state and be idempotent. Lease epochs fence stale messages at the broker, but cannot undo an action already started in the game.

## Refactoring Notes

- All `Program` classes previously duplicated the same helper methods (`TryParseEnvelope`, `TryGetJsonProperty`, `TryReadJsonString`, `ToJsonString`, `EncodeStorage`, `DecodeStorage`). These now live in `libs/igc-core/TaskBusIgcHelper.cs` and are called via `TaskBusIgcHelper.<method>()`.
- `Logger`, `Autopilot`, and `RemoteControlPhysicalInfo` are now `public` with null-safe constructors.
- `AMN.Base` scans inventory from all functional block types with inventories, not just cargo containers.
- Legacy root-level `.csproj` files (`TaskBus.Core.csproj`, `TaskBroker.csproj`, `TaskConsumer.csproj`, `SpaceEngineers.csproj`) are retained for backward compatibility but the MDK² structure under `libs/`, `services/`, `apps/` is the canonical layout going forward.
