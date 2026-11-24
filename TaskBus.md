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

Use the .NET 8 SDK from `global.json` and the Windows Dedicated Server libraries from Steam app `298740`. The script builds the seven PB projects, three libraries, and test project into separate directories under `build/output/`, then runs the tests. The DLLs are compile outputs. The build also creates one paste-ready `*.pb.cs` file per Programmable Block. Each file contains the block script and its shared TaskBus code. Copy the entire file into the in-game editor. The build compiles the generated files inside C# 6 Programmable Block wrappers. Successful GitHub Actions runs include these files in the `taskbus-build` artifact.

### Mining dispatch acceptance test

Use a test world with in-game scripts enabled and two grids in radio range.
Install `AMN.Base.pb.cs` and `TaskBroker.pb.cs` on the base and
`TaskConsumer.pb.cs` on the worker. Power a functional radio antenna on each
grid. On the worker, run `group MiningFleet`. Use a resource such as
`Ore/Iron` in the base cargo and set a threshold with `set Ore/Iron 100`
on AMN.

AMN counts all inventory slots of functional cargo containers, assemblers,
refineries, cockpits and remote controls on the base construct. Cargo on a
ship attached only by a connector is outside that inventory scope.

1. Start below the threshold. Run `status` on the broker and record the task
   ID. Leave the shortage in place through a publication retry. The worker
   should retain the same task ID and the broker should show one open task.
   Check that the worker status shows the requested amount and the base's
   name and coordinates at the time of the request.
2. Run `stage survey` on the worker. Recompile its Programmable Block without
   clearing Storage. Wait for the broker to confirm the lease again. Verify
   that the worker restores the same task and displays `mine` as the next stage. Run `stage mine`, `stage intermediate` and
   `stage deliver` as you complete those actions. Bring the base stock above
   the minimum after the worker reports the broker commit. The stage commands
   record operator confirmation of each action.
3. Create a new shortage. Disable the worker antenna after it receives an
   assignment. While disconnected, a stage command should leave the checkpoint
   unchanged. Wait until the broker reports the lease expired, then enable the
   antenna. The worker should clear the stale lease and receive a new lease
   epoch on a subsequent poll. With no other worker, the task ID should
   remain the same.
4. With a shortage task open, disable the broker block. Raise the base stock
   above the minimum and let AMN scan it, then lower it again. Re-enable the
   broker and watch AMN retry cancellation. The broker should close the old
   request and receive a new request ID if the stock is still below minimum.

5. Verify the inventory boundary. Leave only a docked ship's cargo above the
   selected minimum and confirm the base still requests the resource. Place
   the same resource in a base assembler or refinery output inventory and
   confirm AMN counts it toward the minimum.

Record task IDs, lease epochs and any block errors. Use separate runs for the
workflow, lease-expiry and cancellation checks so their state is easy to
identify in the broker output.

### Worker display

To show a worker's TaskBus assignments on a ShipLink cockpit display, run
`worker <consumer programmable block ID>` on ShipLink. Use the consumer block's
entity ID shown as `Worker block ID` in the consumer output. The setting persists in Storage. Send `worker 0` to disable task
monitoring. The display shows the assignment and its first workflow stage.
Run `status` on the consumer for the actual stage checkpoint.

### Fleet display for Space Engineers combat

Install `ShipLink.pb.cs` on each wingmate's grid with a working radio antenna
and a cockpit text surface set to Text and Images. Run `fleet Alpha` on each
block to enable the roster. It shows each reporting ship's speed and whether
its cockpit is occupied. Reports expire after about five seconds without an
update at normal simulation speed. Run `fleet off` to disable the roster and
stop transmitting status. The setting persists in block Storage.

Fleet names are display filters on the fleet IGC broadcast. Other listeners
in range can receive the reports. For an in-game check, compare the displays
on two ships, enter and leave a cockpit, then power down one antenna and
confirm the other display drops that ship after the timeout.

### Flight control PB

`apps/fcs` is an opt-in cockpit flight controller. Copy the contents of
`build/output/apps/fcs/fcs/FlightControl.pb.cs` into a Programmable Block on the
same grid as the ship's thrusters and gyros. Name the occupied cockpit with
`[FCS]`. Disable competing remote autopilots and clear existing overrides before
engaging. The controller starts off after compilation or reload.

- `normal` enables assisted flight with six-direction thrust, a 10% thrust
  margin when possible, and support and braking checks. The default braking
  budget is 50 m per local axis. It does not measure distance to terrain.
- `manual` gives active pilot axes priority and leaves safety findings as
  warnings. Release an axis to let FCS damp its unwanted motion.
- `aero on` enables experimental external-force estimation for free-flight
  trials. Use `aero off` before docking, landing contact, or mining. It starts
  disabled and does not require a Workshop API.
- `mass` reports current mass and estimated lift limits for level flight and
  nose-down extraction. It uses the current center of mass and available thrust,
  with a 10% force reserve, 1 m/s² of climb authority, and a modeled residual
  torque equivalent to at most a 5 cm offset. This is a lift estimate for the
  current equipment and load distribution, with no allowance for rock contact
  or future cargo shifts.
- `off` releases the actuators and restores cockpit thruster and dampener
  settings. Leaving the cockpit also releases control while the PB is running.

Thrust allocation uses effective thrust and each engine's moment arm from the
ship's measured center of mass. The PB reports the modeled residual moment.
Verify actuator behavior, gyro signs, and instruction usage in game before
operational use, including with each mod enabled. A stopped PB cannot perform
its release routine. For development trials keep the cockpit controls
`Control Thrusters` and dampeners accessible on the toolbar.

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
