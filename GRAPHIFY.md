# Graphify Repository Documentation: TaskBus

This document provides a structured, graph-oriented representation of the TaskBus repository for use with Graphify (AI/neural network code understanding tool).

## Техническое задание ScriptTest

[ТЗ по AMN, FCS, боевой поддержке и Галнету](docs/ScriptTest-Spec.md) содержит
целевую схему программируемых блоков, требования к системе и пять этапов
приёмки. Заключительный абзац ТЗ фиксирует состояние системы на момент
составления. Команды и текущее поведение описаны в [TaskBus.md](TaskBus.md).

## Продолжение работ

- [Прогресс и покрытие сквозных сценариев](docs/Progress.md)
- Локальные результаты: `graphify-out/graph.html`, `graphify-out/GRAPH_REPORT.md`, `graphify-out/graph.json`
- Из корня проекта: `graphify query "AMN TaskBusConsumer MiningRouteLoader FCS"`
- Обновление кода графа: `graphify update .`

## Repository Overview

- **Name**: TaskBus
- **Description**: Kafka-inspired persistent work queue over Space Engineers IGC (Inter-Grid Communication) for Programmable Blocks
- **Type**: MDK² monorepo (Multi-Domain Kernel squared)
- **Language**: C# (.NET 8)
- **Target Platform**: Space Engineers Programmable Blocks (PB)
- **Git Remote**: https://github.com/ProvotorovMaksim/ScriptTest.git
- **Version**: v2.0.4 (from 14.10.2026)

## Repository Structure (Graph Nodes)

```
libs/igc-core/          # Shared mixin library (IGC transport, JSON protocol, Storage helpers)
services/broker/        # TaskBroker PB — domain-neutral task queue broker
services/consumer/      # TaskConsumer PB — domain-neutral task consumer with workflow checkpoints
apps/amn/               # AMN.Base PB — resource deficit producer (mining fleet)
apps/sample/            # SampleMain PB — simple PING/PONG example
apps/ship-link/         # ShipLink PB — ship-to-base connection with cockpit display
TaskBus.Tests/          # Unit tests for TaskBusProtocol
```

## Layer Architecture (Directed Graph)

### Layer 1: Shared SDK (libs/igc-core)
Compiled into every Programmable Block. Provides foundational primitives.

| Node | Type | Responsibility | File | Namespace |
|------|------|----------------|------|-----------|
| TaskBusProtocol | Class | Versioned JSON envelope encoding/decoding | `TaskBusProtocol.cs` | `SpaceEngineers.Libs.IgcCore` |
| TaskBusProducer | Class | Reusable transport-facing producer API | `TaskBusProducer.cs` | `SpaceEngineers.Libs.IgcCore` |
| TaskBusIgcHelper | Class | Shared IGC/Storage helper (envelope parsing, JSON extraction, Base64 storage) | `TaskBusIgcHelper.cs` | `SpaceEngineers.Libs.IgcCore` |
| Logger | Class | Terminal-block logger (trace/warning/error) | `Logger.cs` | `SpaceEngineers.Libs.IgcCore` |
| Autopilot | Class | Remote control autopilot helper with null-safe constructors | `Autopilot.cs` | `SpaceEngineers.Libs.IgcCore` |
| RemoteControlPhysicalInfo | Class | Physical info wrapper for IMyRemoteControl | `Autopilot.cs` | `SpaceEngineers.Libs.IgcCore` |

### Layer 2: Services (services/)
Domain-neutral infrastructure services running on Programmable Blocks.

| Node | Type | Responsibility | File | Namespace |
|------|------|----------------|------|-----------|
| TaskBroker | Class | Queue broker: storage, deduplication, leases, consumer groups | `services/broker/Program.cs` | `SpaceEngineers.Services.Broker` |
| TaskConsumer | Class | Polling consumer: checkpoints, stage workflow, commit ACK | `services/consumer/Program.cs` | `SpaceEngineers.Services.Consumer` |

### Layer 3: Applications (apps/)
Domain-specific applications built on top of services.

| Node | Type | Responsibility | File | Namespace |
|------|------|----------------|------|-----------|
| AMN.Base | Class | Resource deficit producer: scans inventory, publishes deficits | `apps/amn/Program.cs` | `SpaceEngineers.Apps.Amn` |
| SampleMain | Class | Minimal PING/PONG example PB | `apps/sample/Program.cs` | `SpaceEngineers.Apps.Sample` |

### Layer 4: Tests
| Node | Type | Responsibility | File |
|------|------|----------------|------|
| TaskBusProtocolTests | Class | Unit tests for TaskBusProtocol | `TaskBus.Tests/TaskBusProtocolTests.cs` |

## Component Dependency Graph

```
TaskBusProtocol (libs/igc-core)
    ↑
TaskBusIgcHelper (libs/igc-core) ─── depends on ─── TaskBusProtocol
    ↑
TaskBusProducer (libs/igc-core) ─── depends on ─── TaskBusProtocol
    ↑
Logger (libs/igc-core)
Autopilot (libs/igc-core)
RemoteControlPhysicalInfo (libs/igc-core)

TaskBroker (services/broker) ─── depends on ─── TaskBusProducer, TaskBusIgcHelper
TaskConsumer (services/consumer) ─── depends on ─── TaskBusProducer, TaskBusIgcHelper, TaskBusProtocol
AMN.Base (apps/amn) ─── depends on ─── TaskBusProducer, TaskBusIgcHelper
SampleMain (apps/sample) ─── depends on ─── Autopilot, Logger
```

## Message Flow Graph (IGC Events)

### Event Types
- `TASK_PUBLISH` — Producer publishes a new task request
- `TASK_POLL` — Consumer polls broker for available tasks
- `TASK_ASSIGN` — Broker assigns a task to a consumer
- `TASK_RECEIVED` — Consumer acknowledges task receipt
- `TASK_DONE` — Consumer signals task completion
- `TASK_DONE_ACK` — Broker acknowledges task completion
- `TASK_CANCEL` — Producer cancels a pending task
- `TASK_CANCEL_ACK` — Broker acknowledges cancellation
- `TASK_REJECTED` — Broker rejects a task (queue full)
- `TASK_REVOKED` — Broker revokes a lease (stale/fenced)
- `QUEUE_EMPTY` — Broker signals no tasks available
- `TASK_PUBLISHED_ACK` — Broker acknowledges task publication
- `PING` / `PONG` — Sample connectivity test
- `MSG` — Sample text message

### Message Flow: Publish → Assign → Execute → Complete

```
Producer (AMN.Base)
    │ TASK_PUBLISH (with request_id, group_id, resource, steps)
    ▼
Broker (TaskBroker)
    │ Validates deduplication, creates TaskRecord, persists to Storage
    │ TASK_PUBLISHED_ACK → Producer
    │
    │ (on consumer poll)
    ◄──── TASK_POLL ──── Consumer (TaskConsumer)
    │
    │ TASK_ASSIGN (with task_id, lease_epoch, details)
    ▼
Consumer (TaskConsumer)
    │ TASK_RECEIVED → Broker
    │ Executes workflow stages (survey → mine → intermediate → deliver)
    │ TASK_DONE → Broker
    │
    │ TASK_DONE_ACK ◄──── Broker
    │
    │ (on stage complete)
    ◄──── TASK_REVOKED ──── Broker (if lease expired)
```

## State Machine Graph

### Broker Task States
```
QUEUED ──(consumer poll)──▶ LEASED ──(TASK_RECEIVED)──▶ ACTIVE ──(TASK_DONE)──▶ DONE (removed)
   ▲                           │                           │
   │                           │ (lease timeout)           │ (CLEARED via cancel)
   └───────────────────────────┴───────────────────────────┘
                           (requeued with incremented lease_epoch)
```

### Consumer Workflow States
```
Idle ──(TASK_ASSIGN)──▶ Receiving ──(TASK_RECEIVED sent)──▶ Active Task
   │                                                      │
   │ (stage command)                                      │ (stage complete)
   ▼                                                      ▼
Stage N ──(advance)──▶ Stage N+1 ──(all stages done)──▶ Pending Completion
                                                                   │
                                                                   ▼
                                                             TASK_DONE sent
                                                                   │
                                                                   ▼
                                                             TASK_DONE_ACK received
                                                                   │
                                                                   ▼
                                                             Idle (task cleared)
```

## Data Structures Graph

### TaskRecord (Broker internal)
```
Fields:
  Id: string
  RequestId: string
  GroupId: string
  RequesterId: long
  WorkerId: long
  Key: string
  RequestJson: string
  State: string (QUEUED|LEASED|ACTIVE|DONE|CLEARED)
  Attempts: int
  LastDispatchTick: int
  LeaseEpoch: int
```

### TaskBusEnvelope (Protocol)
```
Fields:
  EventType: string
  PayloadJson: string
  MessageId: string (optional)
  TargetId: long (0 = broadcast)
```

### TaskCommit (Broker internal)
```
Fields:
  TaskId: string
  ConsumerId: long
  LeaseEpoch: int
```

## Storage Persistence Graph (PB Storage)

### Broker Storage Format (pipe-delimited lines)
```
SEQ|<sequence_number>
CANCEL|<base64_encoded_request_id>
COMPLETE|<base64_encoded_request_id>
COMMIT|<base64_encoded_task_id>|<consumer_id>|<lease_epoch>
TASK|<id>|<base64_request_id>|<base64_group_id>|<requester_id>|<worker_id>|<base64_key>|<base64_request_json>|<state>|<attempts>|<last_dispatch_tick>|<lease_epoch>
```

### Consumer Storage Format (key-value lines)
```
ACTIVE_ID|<base64_encoded_task_id>
ACTIVE_JSON|<base64_encoded_task_json>
STAGE_INDEX|<index>
STAGES|<base64_encoded_json_array>
GROUP|<base64_encoded_group>
LEASE_EPOCH|<epoch>
REVOKED|<base64_encoded_revoked_task_id>
COMPLETED|<base64_encoded_pending_task_id>
COMPLETED_EPOCH|<epoch>
```

### AMN.Base Storage Format (key-value lines)
```
REQSEQ|<sequence_number>
MIN|<resource_key>=<minimum_value>
REQUEST|<base64_resource>|<base64_request_id>
LOW|<resource>
CLEAR_PENDING|<resource>
```

## Build Graph

```
build.sh
    │
    ├──▶ dotnet build services/broker/broker.csproj ───▶ build/output/TaskBroker.dll
    ├──▶ dotnet build services/consumer/consumer.csproj ───▶ build/output/TaskConsumer.dll
    ├──▶ dotnet build apps/amn/amn.csproj ───▶ build/output/AMN.Base.dll
    └──▶ dotnet build apps/sample/sample.csproj ───▶ build/output/SampleMain.dll

Environment variable: SPACE_ENGINEERS_BIN64 (path to Space Engineers Bin64 folder)
Output directory: build/output/
Configuration: Release
```

## Test Graph

```
TaskBus.Tests/TaskBus.Tests.csproj
    │
    └──▶ TaskBusProtocolTests.cs
        │
        ├──▶ Tests TaskBusProtocol.CreateEnvelope
        ├──▶ Tests TaskBusProtocol.TryParseEnvelope
        ├──▶ Tests TaskBusProtocol.TryGetProperty
        ├──▶ Tests TaskBusProtocol.TryReadString
        └──▶ Tests TaskBusProtocol.TryReadStringArray
```

## Key Algorithms (Graph Nodes)

### Deduplication (Broker)
- Scoped request ID = `producerId:requestId`
- Check `_completedRequestIds` and `_cancelledRequestIds` before creating new task
- Tombstones retained after task removal (max 500 entries each)

### Lease Management (Broker)
- Lease timeout: 180 ticks
- On expiration: WorkerId=0, State=QUEUED, LeaseEpoch++
- Fencing: stale messages rejected if leaseEpoch mismatch

### Consumer Group Assignment (Broker)
- Poll matches by GroupId
- Skips tasks where RequesterId == ConsumerId (no self-assignment)
- First QUEUED task in group is assigned

### Workflow Checkpoints (Consumer)
- Stages defined in task payload `details.steps` array
- Stage index persisted in Storage
- Commit only after all stages acknowledged
- Revoked lease fences further work

## Public API Surface (for Graphify consumption)

### TaskBusProtocol (static class)
- `CreateEnvelope(eventType, payloadJson, messageId?, targetId?): string`
- `TryParseEnvelope(json, out envelope): bool`
- `TryGetProperty(json, propertyName, out rawValue): bool`
- `TryReadString(json, out value): bool`
- `TryReadStringArray(json, out values): bool`
- `Quote(value): string`

### TaskBusProducer (class)
- `constructor(channel: string, send: TaskBusSend)`
- `Publish(eventType, payloadJson, messageId?, targetId?): string` (returns envelope)

### TaskBusIgcHelper (static class)
- `TryParseEnvelope(json, out eventType, out payloadJson): bool`
- `TryGetJsonProperty(json, propertyName, out rawValue): bool`
- `TryReadJsonString(json, out value): bool`
- `ToJsonString(value): string`
- `EncodeStorage(value): string`
- `DecodeStorage(value): string`

### Logger (class)
- `constructor(logBlock: IMyTerminalBlock)`
- `LogTrace(message): void`
- `LogWarning(message): void`
- `LogError(message, errorMessage): void`
- `Clear(): void`

### Autopilot (class)
- `constructor(remoteControl: IMyTerminalBlock)`
- `Accuracy: int`
- `PointPhysicalInfo: RemoteControlPhysicalInfo`
- `OnUpdate(): void`
- `IsArrived(): bool`
- `Go(): void`
- `Go(destination: Vector3D): void`
- `AddDestination(destination: Vector3D): void`
- `Resume(): void`
- `Stop(): void`
- `SetSpeedLimit(speed: int): void`

## Configuration Constants

| Component | Constant | Value | Description |
|-----------|----------|-------|-------------|
| TaskBroker | CommunicationTag | "SE1-SHIP-LINK" | IGC channel |
| TaskBroker | MaxTasks | 100 | Max concurrent tasks |
| TaskBroker | LeaseTimeoutTicks | 180 | Lease expiration in ticks |
| TaskConsumer | PollIntervalTicks | 30 | Poll frequency in ticks |
| TaskConsumer | CommunicationTag | "SE1-SHIP-LINK" | IGC channel |
| AMN.Base | CommunicationTag | "SE1-SHIP-LINK" | IGC channel |
| AMN.Base | GroupId | "MiningFleet" | Consumer group for mining |

## Delivery Guarantees

- **At-least-once assignment**: Tasks may be delivered more than once
- **Producer-side request deduplication**: Same request_id not processed twice
- **Consumer-side durable checkpoints**: Workflow progress persisted in PB Storage
- **Lease epochs**: Fence stale messages; incremented on reassignment
- **Not exactly-once**: Physical world actions may repeat if PB loses power after action but before checkpoint

## Backward Compatibility

Legacy root-level `.csproj` files retained:
- `TaskBus.Core.csproj`
- `TaskBroker.csproj`
- `TaskConsumer.csproj`
- `SpaceEngineers.csproj`

Canonical layout: `libs/`, `services/`, `apps/` under MDK² monorepo pattern.

## Version History

| Version | Date | Changes |
|---------|------|---------|
| v2.0.4 | 14.10.2026 | Build fix (OutputType Library), .NET 8 retarget, game path placeholder |
| v2.0.3 | 20.07.2024 | Linux support, cleaner .csproj, useful sample added |

## Graphify Metadata

- **Repository Type**: C# Monorepo (MDK² pattern)
- **Primary Domain**: Space Engineers Programmable Blocks
- **Communication Protocol**: IGC (Inter-Grid Broadcast) with JSON envelopes
- **Persistence**: PB Storage (pipe-delimited, Base64-encoded)
- **AI Consumption**: This file is optimized for Graphify neural network code understanding
- **Key Relationships**: Dependency graph, message flow graph, state machine graph, storage format graph