# ScriptTest installation and acceptance

Open a successful **Build TaskBus** run for the commit you want to test
and download the `taskbus-build` artifact. Extract it and locate
`mdk-scripts/`. The ten directories below each contain a `script.cs`.

Use a Space Engineers test world with in-game scripts enabled. Create
one programmable block per role. Open **Edit** on each block, paste the
contents of its corresponding `script.cs`, and run **Check code**.
Record the build commit and any errors before running the scripts.
When updating a block, keep its configuration and Storage so active
TaskBus assignments and relay settings can recover.

| File under `mdk-scripts/` | Role and blocks for initial setup |
| --- | --- |
| `broker/script.cs` | TaskBus broker. Use a programmable block and a working radio antenna for direct IGC. Add a local `[TASKBUS BACKUP]` LCD for operator backups. |
| `consumer/script.cs` | TaskBus consumer, mining point or hauler. Use a working radio antenna for direct IGC. For the point, add a local `[AMN STATUS]` LCD to monitor jobs. For the hauler, configure cargo, pickup and delivery remote controls and connectors as described in the [AMN setup](../TaskBus.md#mining-point-dispatch). |
| `producer/script.cs` | Diagnostic TaskBus producer. Use a programmable block and a working radio antenna. |
| `galnet/script.cs` | Galnet relay. Use a programmable block per node and a working IGC link to each configured peer. Use laser antennas for the laser-link tests. See [Galnet routing and configuration](../TaskBus.md#galnet-routing-core). |
| `amn/script.cs` | AMN base. Use a working antenna and local inventory blocks, such as cargo containers or refineries. An `[AMN STATUS]` LCD is optional. |
| `platform/script.cs` | AMN drilling and scout platform. Use `[AMN PLATFORM]` for the local controller, a forward `[AMN SCOUT]` camera, drills named with the `[AMN DRILL]` prefix for mining, and the applicable route and docking blocks. See [platform configuration](../TaskBus.md#amn-platform-worker). |
| `fcs/script.cs` | Flight control. Use a cockpit named with `[FCS]` on the same grid as the thrusters and gyros. For docking, add `[FCS DOCK]` and configure the berth. See [FCS configuration](../TaskBus.md#flight-control-pb). |
| `ship-link/script.cs` | ShipLink fleet display. Use a working antenna and a cockpit text surface. See [fleet configuration](../TaskBus.md#fleet-display-for-space-engineers-combat). |
| `radar/script.cs` | Radar. Use configured cameras and an optional `[RADAR LCD]`. Gimbal also uses an azimuth rotor and an elevation hinge. See [Radar setup and acceptance](Radar.md). |
| `sample/script.cs` | Diagnostic sample. Use a working antenna, a remote control named `RemCon` and the programmable block named `Prog`. |

Start with direct TaskBus radio communication using the
[mining dispatch acceptance test](../TaskBus.md#mining-dispatch-acceptance-test).
Then exercise Galnet with a primary route and an alternate route.
Run the Radar Fixed and Gimbal checks separately. For FCS, check
braking, docking, charging and departure before attempting the complete
AMN mining and delivery cycle. The
[system specification](ScriptTest-Spec.md#порядок-реализации-и-приёмка)
defines the system-level acceptance checks.

## Known limitations

- TaskBus broker recovery uses an operator-driven transfer to a selected
  backup broker. Automatic failover remains open.
- The mod-backed AMN ore survey requires a detector exposing
  `Survey.DetectedOre`. The configured routes, cargo transfer and docking
  still need acceptance in the intended world and physics setup.
- Radar shares its status locally. ShipLink integration is pending.
  Gimbal searches in azimuth at the current elevation.
- A successful build verifies compilation and automated tests. Record
  runtime acceptance separately for each scenario, including laser links,
  camera reacquisition and drive stop.
