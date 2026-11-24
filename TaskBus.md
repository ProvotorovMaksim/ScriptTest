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

Use the .NET 8 SDK from `global.json` and the Windows Dedicated Server libraries from Steam app `298740`. The script builds the nine PB projects, three libraries, and test project into separate directories under `build/output/`, then runs the tests. The DLLs are compile outputs. The build also creates one paste-ready `*.pb.cs` file per Programmable Block. Each file contains the block script and its shared TaskBus code. Copy the entire file into the in-game editor. The build compiles the generated files inside C# 6 Programmable Block wrappers. Successful GitHub Actions runs include these files in the `taskbus-build` artifact.

### Mining workflow reference

[PAM by Keks](https://steamcommunity.com/sharedfiles/filedetails/?id=1553126390)
is the reference for the mining cycle: teach a route between a stationary
berth and the work site, configure the excavation area, then repeat trips
while unloading and recharging at home. In ScriptTest, TaskBus owns dispatch
and checkpoints, FCS owns the last metres and the berth battery cycle, and
the mining script owns route following and drilling.

### Ore survey input

The [Ore Detector GPS Script API](https://steamcommunity.com/sharedfiles/filedetails/?id=3695937446)
documents the string terminal property `Survey.DetectedOre`. It returns
one GPS record per line with the ore name and world coordinates.
`TaskBusIgcHelper.TryReadOreGps` parses these records for a survey adapter.
Workshop availability and runtime behavior still need a check in the target
world. Run `survey` on AMN to read working detectors on its construct and
persist up to 256 distinct GPS observations. `survey list` shows the count
and the first 15 records. Use `survey send <block ID>` to transfer the
catalog to another AMN block. Read `Survey block ID` from the destination's
`survey list` output. The sender retries one record at a time and both
the pending queue and the receiving catalog persist across reload. A full
catalog leaves unaccepted records pending on the sender. Use `survey cancel` to
stop a transfer. A survey hit is a catalog observation. Route planning should use a
separate approach point outside the voxel.

The broker stores the UTC deadline of each newly issued or renewed
lease alongside the simulation tick check. Loading Storage after downtime
requeues expired leases and fences their old epochs. Legacy records without
a UTC deadline keep the existing simulation tick check until renewed.
Assignments carry this deadline so workers stop acting on expired leases
even before receiving a revocation. A delayed older refresh cannot replace
a newer deadline for the same broker, task and lease epoch. Workers save
the latest deadline and require a newer confirmation after a restart.

Use `broker pause` on the broker PB before planned maintenance. It returns
leased parent and standalone work to the queue, removes children from the
old parent leases, and stops accepting traffic. Pause survives recompiles.
Workers with previously delivered assignments may take until their
confirmation deadline to stop. Use `broker resume` on the same PB to
start issuing work again.

For a planned handoff, add a dedicated `[TASKBUS BACKUP]` text panel to
the source and destination grids. A shared panel works when both brokers
are on the same grid. Pause the source and run `broker handoff`. It
exports a verified snapshot and durably retires the source. Check that
`status` reports Retired before activating the destination. A retired
broker can export its data again and rejects resume and import. Use
`broker export` for a backup when you intend to keep using the source.
If using two panels, copy the complete text without editing it. Pause a fresh empty
destination and run `broker import`. A successful import restores the
queue and its deduplication history and leaves the destination paused.
Use the broker selection procedure below to switch idle workers. If a
worker still has an assignment or a pending completion, run
`broker restored <new PB ID>` after the restored broker resumes. The
worker invalidates local lease confirmation and retains its checkpoint
until the restored broker confirms or revokes it. If the relay changed,
configure the destination Galnet relay on each switching block. On
AMN.Base, `broker restored <new PB ID>` keeps the original request IDs
and pending cancellations and retries them against the restored queue. Use this command only with the verified copy
of the original queue. Keep
the source paused. The snapshot carries the earliest safe resume time.
It is at least 30 seconds after pausing a broker that has issued work,
and covers any later known lease deadlines. A legacy snapshot without
this timestamp waits 30 seconds after import. `broker resume` reports
the UTC deadline if invoked early. Transfer is operator-driven, and the
backup must fit in one text panel.

### Galnet routing core

`GalnetTopology` computes next hops from current, reciprocal neighbor
advertisements. The shortest path wins, with node ID as the tie-breaker. Route computation
skips locally unavailable next hops and excludes the previous hop during
forwarding, so it can use an alternative while link advertisements converge.
Packets start with a budget of eight hops, and each relay searches only
within the remaining budget. If IGC rejects a send to the chosen peer,
the relay tries one reserve next hop immediately. The sender still
retains the packet until end-to-end acknowledgement or expiry.
A node's generation and sequence reject repeated or reordered updates.
Expiry values are in the receiving node's time domain. The core retains
up to 128 node versions and accepts up to 32 neighbors per node. When
the table is full, an unfamiliar node can displace the oldest expired
record. Current records remain eligible for routing. Install `GalnetRelay.pb.cs` on each test relay. Configure both ends with
`peer add <PB ID>`, using the ID displayed by each relay. `route <ID>`
shows the current next hop. The relay's default peer list reports direct
heartbeat status. Run `network status` for each queued delivery's current
route and next hop, or to see that it is waiting for a route or the local
application.
Use `send <node ID> <text>` to check end-to-end delivery. The sender keeps
up to 16 pending messages in Storage. Timed messages are admitted while
fewer than 12 total messages are pending, leaving four slots for regular
deliveries. The relay clears expired entries before checking capacity
and retries pending messages via the current
route. Repeated regular application sends to the same destination with the
same payload reuse the pending packet and its original deadline, including
after a relay restart. The relay retries on its own schedule when an
application submits the same pending message again. Delivery is retried
until acknowledgement or until
the five-minute UTC deadline
expires. Lease polls, assignments, receipt notices and empty-queue replies
expire after 20 seconds and use the short-lived queue limit. Later polls
refresh the current lease state. Requests and completion messages keep
the regular deadline. A fleet heartbeat replaces the earlier queued
heartbeat for the same destination and fleet. Delayed older samples leave
the newer one queued. Upgrade all relays together. Older diagnostic packets without
a deadline are discarded. The receiver retains up to 64 delivery IDs until their original deadlines
for duplicate suppression across restarts. Once 48 slots are occupied,
only addressed TaskBus messages can claim a remaining slot. If every
slot is occupied, new
messages wait for the sender to retry after a slot expires. Text delivery is a diagnostic. Use `app <node ID> <payload>` to deliver
up to 1024 characters to a working local programmable block named
`[GALNET APP]`. The recipient script gets `galnet recv <origin> <ID> <payload>`.
The relay acknowledges a successful local invocation. Application handlers
must still make their own writes idempotent and confirm business outcomes.
The broker can receive an addressed envelope via
`galnet recv <remote relay> <packet ID> taskbus <source PB ID> <envelope>`
when its grid has a working relay named `[GALNET RELAY]`. Name the broker
`[GALNET APP]` on that grid so the relay invokes it. Replies are deferred
until the next broker invocation to avoid calling back into an executing
relay. Use configured, trusted relays for this test. Application IDs in
the payload are not cryptographically authenticated. The mining platform and TaskConsumer can use this bridge. On its grid name the relay
`[GALNET RELAY]` and the worker PB `[GALNET APP]`. Add
`GalnetBrokerRelay=<broker-side relay PB ID>` to the worker PB's Custom Data
and recompile. Run `broker <TaskBroker PB ID>` on the worker while idle.
The worker queues responses until its next invocation. The local bridge
uses memory queues, with task checkpoints and completion recovery still
owned by TaskBus. Direct IGC is used when no relay ID is configured.
AMN.Base can use the same bridge for publishing, querying and cancelling
deficit requests. Use a base-side relay named `[GALNET RELAY]`, name the
AMN.Base PB `[GALNET APP]`, and configure `GalnetBrokerRelay` and
`broker <TaskBroker PB ID>` as on a worker. A base and broker on the same grid can share one relay. Add one
`App=<programmable block ID>` line for each local TaskBus application to
the relay Custom Data, then recompile. Point `GalnetBrokerRelay` to the
shared relay. It queues addressed local messages and dispatches them by
block ID on its next retry update. If any `App=` entries are configured,
include every local TaskBus recipient on that relay. Non-TaskBus packets
still go to `[GALNET APP]`. Direct ore catalog transfers
and radio diagnostics continue to use radio.

To check failover in a world, connect the worker and broker relays
through two independent satellite relays. Verify that `route <broker relay ID>`
names the primary next hop. Turn off the antennas on both paths, queue a
small test request, and check that the worker-side relay reports it as
pending. Restore the reserve path first and check that the next hop
changes, the request reaches the broker, and the pending count returns
to zero after the acknowledgement. Compare task IDs and quantities at the
broker before and after reconnecting to check for duplicate processing.
Repeat with the primary path restored and a fresh test request.

For a stationary laser link, add a line such as
`Laser=Laser A|GPS:Peer:1:2:3:` to the relay Custom Data and recompile.
Use the exact local antenna name and the remote antenna coordinates.
The relay retries idle antennas every 60 Update10 calls while leaving
active connections alone. `laser status` shows configured antenna states.
Configure logical peers separately. Verify real link recovery in game.

### Select a broker

Update all brokers sharing the channel before using broker selection. In a
world with multiple TaskBus brokers, run `status` on the intended
broker and note its `Broker block ID`. Run `broker <ID>` on AMN and its
workers before creating requests. Each block stores the selection and sends
its TaskBus traffic to that broker ID. `broker 0` restores broadcast mode.
Resolve existing tasks, reports and cancellations before switching brokers.
Messages travel over IGC broadcast, with the recipient ID in the envelope.

### Mining-point dispatch

Before creating deficits, run `dispatch point` on the requesting base AMN
and `group MiningPoints` on the TaskConsumer block at the mining point.
The point leases a parent request with `survey`, `mine` and `transport`
stages. While the point is idle, run `point auto` to automatically publish
one child for the current stage to `Scouts`, `MiningFleet` or `Haulers`
respectively. The child request ID is stable across retries and restart.
After the child completes, confirm survey and mining on the point consumer.
Set `MiningBuffer=<local cargo container name>` in the mining point PB to
advance mining automatically after the child completes and the buffer
contains the requested resource and quantity. The same inventory check
applies to manual `stage mine` with this setting.
Transport with a required base receipt completes automatically once the
receipt and hauler completion are both confirmed.
For new point requests, transport confirmation also requires the original
base to report stock at or above its requested minimum. This is a stock-level
confirmation. It does not identify the source of individual items.
`point manual` restores operator-only stage confirmation. `dispatch fleet` restores the direct
fleet workflow. AMN saves the dispatch setting and refuses to change it
while requests or cancellations are outstanding.

During a new request's survey stage, select the deposit with
`site GPS:Ore:X:Y:Z:` and record a distinct approach point outside the voxel
with `approach GPS:Name:X:Y:Z:`. Both points survive a restart and are included
in the mining assignment. This records an operator-selected approach point.
On an AMN drilling platform or a generic worker in `MiningFleet`, name the route Remote Control `[AMN ROUTE]`
and run `route approach` with a confirmed mining lease. This appends the
approach to the configured waypoint list while autopilot is off. A previous
AMN approach must be removed before loading a different task. Review route
and terrain clearance before enabling autopilot. On a drilling platform,
use `mine cruise` after undocking to start the reviewed One Way outbound
route. It checks the saved endpoint against the current mining assignment
and stops the supervised autopilot on lease loss.

Use a second, empty Remote Control named `[AMN RETURN]` for the return leg.
With both autopilots off, `route return` copies the configured outbound
waypoints in reverse order, excluding the final ore approach. The first
outbound waypoint should be the return handoff position near the mining
point. Review clearance for the return controller's mounting position,
the loaded ship and current obstacles before launching. Final docking
is handled separately by FCS. On a drilling platform, turn the drills off,
return to within 10 m of the ore approach, and use `mine return` to start
the reviewed One Way route. The platform compares it with the outbound
route before launch and stops its autopilot if the lease is lost. Use
`ore stop` to cancel the supervised flight.

On the mining point, run `site remember` after its survey child completes
and both waypoints are set, before confirming the survey stage. This
registers an operator-approved, reusable site for the requested ore at
that point. The local catalog holds up to 32 deposits across all ores.
Future leases for that ore start at mining when the nearest recorded
entry is within the point's search radius. Set `KnownSiteRadius=5000`
in the point PB's Custom Data to configure the radius in metres. The
default is 5000 m, measured from the point grid origin. If no entry
qualifies or the origin is unavailable, the point starts a survey. Use `site forget GPS:Iron:X:Y:Z:` to remove one
depleted deposit by its ore GPS, or `site forget Ore/Iron` to remove all
entries for that ore.
To seed it from an already verified deposit, run
`site add GPS:Iron:10:20:30: | GPS:Entry:10:20:60:` on the automatic
mining point. Supply both the ore and its distinct, reviewed entry
position. The command applies to future leases.

A point publishing a child includes `parent_task_id` and
`parent_lease_epoch` in its `TASK_PUBLISH` payload. The broker admits new
children only from the block holding that lease in `MiningPoints`.
Retries of an already admitted request return its existing result. When the
parent lease expires or the parent closes, the broker cancels its outstanding
children, saves their cancellation records and revokes current assignments.
A worker that misses the notification receives the revocation when polling
with its old assignment.

Once base stock reaches its minimum in point mode, AMN queries the
existing request instead of cancelling a known task. The point can then
finish transport and commit it. The base clears its local request when
the broker reports a terminal state. A request never seen by the broker
is retired through the existing cancellation exchange.

On AMN.Base the cockpit and an optional local `[AMN STATUS]` panel
show stock targets and each open resource request's last broker state,
rejection reason or pending cancellation. For a mining-point request
with an outstanding child, the broker also reports survey, mine or
transport and the child queue state. After a restart or broker
switch, open requests display Awaiting broker until the next reply.

Name a text panel on the point consumer's grid exactly `[AMN STATUS]`
to show the parent task, current stage, lease and last broker state for the
child request. The panel also shows when the local radio is unavailable.
The resource amount shown is the requested amount.

On an AMN grid with a saved ore catalog, run `survey nearest Iron 5000`
to print the nearest stored Iron GPS within 5000 m of the grid. A missing
result leaves survey coverage unknown. A stored observation supplies a
candidate coordinate for verification at the site. An ore deficit request
includes the nearest candidate within 5 km of the requesting grid when
available. A mining point forwards it to the survey assignment, where it
appears as a GPS hint. A site with both saved waypoints still goes straight
to the mining stage.

In mining-point dispatch mode, AMN.Base reports replenishment as soon as
its inventory scan first reaches the requested minimum. It repeats stock
checks while the parent request is open, so a temporary loss of Galnet
does not prevent the broker from later recording a qualifying reading.

New hauler assignments start at `pickup`. After loading the cargo, run
`stage pickup`, then run `stage deliver` after unloading. The pickup
checkpoint survives a recompile. Resume delivery after broker lease
confirmation. To check the actual load at pickup, set
`HaulerCargo=<local cargo container name>` in the hauler PB Custom Data
and recompile. Confirming pickup then requires the requested resource
and amount in that container. The hauler saves the measured pickup amount
and checks that the requested quantity has left the same container before
accepting `stage deliver`. Base stock confirmation is still required to close
the parent request. In `point auto` mode, the point closes the transport
stage once the broker confirms both the hauler completion and base stock.
Assignments picked up without a recorded measurement
can still be confirmed manually.

Set `HaulerDock=<local connector name>` and recompile to require docking
at the assigned mining grid for pickup and the assigned base grid for
delivery. Both station connectors must be on the grids identified by the
assignment. A transport task published before pickup grid IDs were added
must be reissued before it can use the pickup docking check.

With both `HaulerCargo` and `HaulerDock` configured, set `HaulerAuto=true`
to confirm pickup and delivery automatically when the required checks pass
under a recently confirmed lease. A delivery without a saved pickup
measurement still needs operator confirmation.

Set `HaulerLoadFrom=<mining point cargo container name>` to load
automatically while docked at the assigned mining grid. The hauler moves
only the shortage for the requested resource and confirms pickup after
measuring the resulting load.

Set `HaulerUnloadTo=<base cargo container name>` to transfer cargo
automatically during delivery. The container must be uniquely named on the
assigned base grid and connected through the conveyor system. Transfers
are bounded by the remaining delivery quantity and cargo is measured again
before the delivery stage completes.

Transport assignments list the mining point and its position at dispatch
alongside the delivery base and its position at request time. These positions
are grid references for route planning. Configure docking approach waypoints
separately.

Configure a reviewed approach for each station with
`HaulerApproach.<grid ID>=GPS:Name:X:Y:Z:` in hauler Custom Data. Run
`route station` under a confirmed lease to append the current station's
approach to a local Remote Control. Use `[AMN PICKUP]` for the pickup leg
and `[AMN DELIVERY]` for delivery. Keep autopilot off while loading the
approach and remove the old AMN waypoint before preparing a new task.
After reviewing the loaded route and undocking, run `route cruise` to
start that leg's Remote Control in One Way mode. It checks the final waypoint against
the confirmed assignment and rejects a connected ship or another
active controller. Use `route stop` or the Remote Control to stop cruise. The hauler
checks the lease and leg while supervising a cruise it started and
switches that autopilot off if confirmation is lost. On restart, the hauler stops its previously supervised local autopilot
and requires a new `route cruise` command after lease confirmation.

Name the hauler FCS programmable block `[AMN FCS]` and record each station
berth with FCS while connected. At the final approach, run `route dock`
on the hauler to request FCS docking for the current assignment. FCS
selects the recorded berth for that grid and applies its usual final
approach checks. The hauler passes the current broker lease deadline and
requests cancellation if it loses the lease. Update the hauler and its
local FCS together. Turn off cruise autopilot and position the ship in the
final approach envelope before requesting docking. Set
`HaulerAutoDock=true` to request that handoff once after the supervised
cruise controller disengages within 2 m of its endpoint, the ship slows
to 0.35 m/s or less, and the lease is still confirmed. Place the endpoint
so the ship arrives inside the FCS final approach envelope. Use
`route stop` to cancel a supervised leg or the matching leased FCS approach.

With `HaulerDock` configured, use `route depart` after pickup while still
connected to the assigned mining point. FCS checks the selected berth and
the departure charge before disconnecting. If charge is low, repeat the
command after charging. For automatic retries, set `HaulerAutoDepart=true` or run
`route depart auto`. The hauler rechecks the lease and the pickup
connection before each request. Use `route stop` to suspend automatic
departure for the current session. Starting the delivery cruise remains
a separate command.

### AMN platform worker

Install `AmnPlatform.pb.cs` on a drilling platform. It accepts both
`Scouts` and `MiningFleet` assignments, one lease at a time. Use
`broker <ID>` if the world has multiple brokers. Name a local flight
controller `[AMN PLATFORM]` and a forward camera `[AMN SCOUT]`.
`probe <metres>` samples the camera ray and reports whether the ship is
in natural gravity, the detected entity type and the hit position.
Camera hits identify geometry. Ore observations come from the ore
detector adapter.

Name the drills managed by AMN with the `[AMN DRILL]` prefix. With the
platform undocked and holding a confirmed mining assignment with an
approach waypoint, use `mine drills on` to enable those drills while
piloting the pass. Use `mine drills off` to stop them. The platform also
disables its named drills on restart or if the mining lease or stage
changes. Set `MiningCargo=<local cargo container name>` to measure the
requested resource in a dedicated inventory and stop the drills when its
requested amount is reached or the inventory is at least 95% full. This
counts existing cargo in that inventory and does not complete the assignment.
Set `MiningLaneRadius=<1 to 100 metres>` to permit drilling only when the
platform grid origin is within that distance of the segment from the
assigned approach to the surveyed ore position. Departure from this lane
stops the managed drills. For a supervised pass, prepare one reviewed
endpoint in a local `[AMN MINE PASS]` Remote Control, set One Way and a
speed limit at or below 1 m/s, then settle within 5 m of the approach.
Run `mine plan` to load a candidate endpoint from the assigned ore GPS.
It offsets for the controller's current position relative to the grid
origin and preserves manually prepared waypoints. It leaves the autopilot
and drills idle while the operator checks alignment and clearance.
Run `mine pass` to start both the drills and the controller. The pass
stops on completion, lease loss, lane or cargo limits, or speed above
1.5 m/s. It also stops if it advances less than 0.5 m toward its endpoint
over 60 scheduled updates (about 10 seconds at normal simulation speed).
Use `mine drills off` to stop the drills and the supervised pass.
Review ship alignment, drill coverage and controller collision settings
before starting. To back out along a reviewed lane, set up
`[AMN MINE RETREAT]` in One Way and Backward mode with one waypoint at the
assigned approach and a speed limit at or below 1 m/s. Alternatively,
run `mine retreat plan` inside the assigned lane to load a waypoint
adjusted for the offset between the pass and retreat controllers.
Planning leaves the controller idle and preserves manually loaded routes.
Stop the drills, align the controller's rear with the approach and run `mine retreat`.
Supervision stops retreat on lease loss, lane exit, excessive speed or
lack of progress. Use `ore stop` to cancel the supervised flight.
Set `MiningAutoRetreat=true` to attempt the reviewed retreat once when
a supervised pass ends within 2 m of its endpoint and the ship settles.
Automatic retreat uses the same start checks as `mine retreat`. Fault
stops and PB restarts require a manual decision. Use `ore stop` or
`mine drills off` to cancel a pending automatic retreat.

Set `MiningAutoReturn=true` to attempt the prepared return route once
after a supervised retreat reaches its waypoint under the same lease.
The platform must settle within 2 m of that waypoint. The usual
`mine return` checks still apply, including an exact match to the
reviewed reverse route. A failed start requires operator intervention.
`ore stop` cancels the pending return.

After returning to the assigned mining point, stop the drills and enter
the FCS final approach envelope. Run `mine dock` to request docking at
the station identified by the mining assignment. The platform needs
a local `[AMN FCS]` PB and a previously recorded berth for that grid.
With `MiningAutoDock=true`, the platform requests the same FCS docking
once after it settles within 2 m of the final prepared return waypoint
under the same lease and with flight controls idle. Place that
waypoint inside the FCS final approach envelope. FCS runs its usual
docking checks. AMN sends the current broker lease deadline to FCS,
which stops the approach if that deadline expires. AMN forwards later
confirmations for the same lease before expiry. FCS caps the approach at
30 seconds of wall time, even with refreshed confirmations. Lease loss or
`ore stop` requests cancellation of the matching approach. Upgrade
the platform and its local FCS together. Manual `dock drone` remains
available on FCS.
Set `MiningAutoUnloadOnDock=true` to arm automatic unloading when the
platform requests FCS docking. The platform waits up to 180 scheduled
updates for its connector to report a physical connection to the
assigned mining point under the same lease. It then uses the existing
automatic unloading and completion checks. `mine unload stop` cancels the
wait or stops an ongoing unload. Configure both cargo container names.
The pending wait does not survive a PB restart.

Set `MiningUnloadTo=<mining point cargo container name>` and run
`mine unload` while docked there with drills off. Each command transfers
up to one stack of the requested resource from `MiningCargo` into the
named conveyor-connected buffer. The platform counts the measured decrease
in its cargo and increase in the buffer, bounded by the attempted quantity.
The tally resumes after confirmation of the same lease. With
`MiningUnloadTo` configured, `stage mine` and `mine finish` require
the full recorded deposit and a connection to the assigned point.
Run `mine unload auto` to repeat confirmed transfers and complete the
mining stage when the full quantity is deposited. It stops on lease or
connection loss, a transfer without measured progress, or a PB restart.
Use `mine unload stop` to stop it manually. A restart during the transfer itself
can require manual inventory reconciliation.

For manual survey sampling, name the mod-enabled detector `[AMN ORE]`.
With a confirmed ore survey assignment, run `ore task` to use its ore
type. Repeating it for the same lease keeps completed samples. Sampling
pauses when that lease is no longer confirmed. Run `ore report` to
complete the survey child with sample counts and one observed GPS, when
available. The broker retains this report and returns it to the mining
point with the child status. After reviewing the report, use `site report`
on the mining point to select the reported ore GPS. New reports can also
include the platform position from the same positive sample. Use
`approach report` to select that position after reviewing route clearance,
or record a separate approach waypoint manually.
If the completed survey needs another flight, run `survey retry` on the
mining point. It clears the current site and approach selections and
issues a new survey child under the same parent lease.

Run `ore plan 25 GPS:Center:X:Y:Z:` during a confirmed task survey to
list a 3×3 sweep around an operator-selected clear center. The plane is
perpendicular to local gravity, or aligned with the ship's up axis in
space. Previously sampled positions are omitted. The GPS points are for
route review and do not have terrain clearance checks. The plan is saved
with the survey. Use `ore route` to load one point into a dedicated local
Remote Control named `[AMN SURVEY ROUTE]` in One Way mode. Turn autopilot
off before loading a point, then run `ore fly` to start the reviewed leg.
The platform stops its supervised survey autopilot after lease loss or a
PB restart. Use `ore stop` to cancel the leg and automatic sampling.
A completed sample within 2 m of the point, measured at the remote, advances
the saved plan. Repeat `ore route` for the next point.

After reviewing all legs for clearance, run `ore sweep` to fly and sample
the remaining points in order. Before each flight the platform looks for
a working local camera whose name starts with `[AMN SCOUT]` and can scan
the next waypoint. Include cameras covering the sweep directions. The
sweep waits briefly for a usable scan and halts on a hit. A clear ray
checks one line and the operator still reviews clearance for the hull.
It advances only after a completed sample
at the planned waypoint. Lease loss, stopping outside the waypoint tolerance
or a sample failure stops the sweep. It stays stopped after a restart.

Use `ore auto on` during a task survey to start samples automatically
once the platform is moving at 0.25 m/s or less and is at least 10 m from
previous sample locations. Hold position for the detector refresh. Use
`ore auto off` to stop automatic sampling. Re-enable it after a recompile.

During a confirmed survey, run `ore refine 25` to generate up to nine
follow-up sample waypoints 25 m outside the observed outline. The route
lies in the plane through the current platform controller, so first
position the ship at a suitable flight height. Points within 10 m of a
previous sample or another planned point are omitted. The generated
route is saved. Review it before running `ore sweep`; the usual lease,
clearance and detector refresh checks still apply.

Use `ore samples` to list the detector GPS positions in acquisition
order, with the result of each completed sample. A position marked
`no report` means the detector did not list the requested ore during that
sample. Earlier saves retain their aggregate count and list individual
positions as `unknown`. New results survive a recompile.

Run `ore bracket 100` to propose up to nine additional detector samples
between positions that reported the requested ore and positions that
reported none, using pairs 20 to 100 m apart. The planner prefers nearby
opposite readings, skips old or crowded sample positions, and estimates
the survey remote waypoint using the current detector-to-remote offset.
Review the proposed flight path before running `ore sweep`. Repeat after
sampling to narrow open intervals. Opposite detector results suggest a
change in detection and may depend on range, orientation or terrain.

Use `ore perimeter` to list the convex outline of the reported ore GPS
positions and their vertical span. The outline uses the local gravity
plane or, in space, the ship plane at the time of the command. Vertices
are displayed at the midpoint of the reported heights. Use the outline
to plan more samples. The area between reports is unverified and the
vertices do not define a cleared flight route or a drill plan.

For standalone surveys use `ore survey Iron`. Move to the first observation position and run
`ore sample`. Hold position for four seconds. Repeat from positions at
least 10 m apart. The worker counts positive views and distinct GPS hits
in persistent PB storage. A recompile restores completed samples and the
spacing checks. A sample still waiting for detector refresh must be repeated.
Starting a new survey replaces the previous survey. Empty readings are
recorded as unknown coverage. This command
does not infer that a repeated GPS hit is a fresh detector result.
Run `ore bounds` to see GPS coordinates for the two corners of the
axis-aligned box enclosing the distinct ore reports from this survey.
These corners are for inspection. Coverage between and beyond the
reports is unknown, and the bounds do not establish a flight corridor.

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
entity ID shown as `Worker block ID` in the consumer output. Send `worker 0`
to disable task monitoring. Both the worker and broker selections persist. In a multi-broker world, run `broker <ID>` on ShipLink with the
same ID as its worker so another broker's traffic cannot update this display.
The display shows the assignment and its first workflow stage. For mining
requests it also shows the requested amount and the current range to the
base position recorded in the request. Run `status` on the consumer for
the actual stage checkpoint.

### Fleet display for Space Engineers combat

Install `ShipLink.pb.cs` on each wingmate's grid with a working radio antenna
and a cockpit text surface set to Text and Images. Run `fleet Alpha` on each
block to enable the roster. It shows each reporting ship's speed and whether
its cockpit is occupied. Reports expire after about five seconds without an
update at normal simulation speed. Run `fleet off` to disable the roster and
stop transmitting status. The setting persists in block Storage.

Assign `assist on` and `assist off` to ShipLink toolbar actions to send or
clear a manual assistance request. Wingmates see reporting ships marked
`[ASSIST]` at the top of the roster until the request is cleared or reports
expire. Changing fleets or restarting ShipLink clears the local request.

Fleet names are display filters on the fleet IGC broadcast. Other listeners
in range can receive the reports. For an in-game check, compare the displays
on two ships, enter and leave a cockpit, then power down one antenna and
confirm the other display drops that ship after the timeout. Test both
assistance commands and confirm the other display adds and removes the marker.

To share a manually observed in-game contact over Galnet, name the local
relay `[GALNET RELAY]` and the recipient ShipLink PB `[GALNET APP]`, and join
the same fleet on both ships. Run `contact <recipient relay ID> GPS:Name:X:Y:Z:`
on the sending ShipLink. The recipient display shows the sender relay and
coordinates for up to 30 seconds from the send time. Fleet names filter the
display and do not authenticate the sender.

Use `contact track <relay ID> <mark name>` to keep an active fleet mark
in the cockpit display. ShipLink shows its age, range and coordinates
relative to the cockpit axes. An expired selection remains visible as
`LAST KNOWN` until it is refreshed by the same source or cleared with
`contact untrack`.

For ongoing fleet heartbeat through Galnet, put up to four
`GalnetFleetPeer=<wingmate relay ID>` entries in ShipLink Custom Data and
configure each peer reciprocally under the same fleet name. ShipLink sends
a status snapshot about once every six seconds at normal simulation speed.
After its 12-second deadline, the last received snapshot stays visible
as stale without an active assistance flag.

Name a local forward camera `[SHIPLINK SCOUT]` and run
`hazard scan <wingmate relay ID> <metres>` to share one camera hit with
a configured Galnet fleet peer. The receiver shows a `[HAZARD]` GPS mark
for up to 30 seconds after sending. A scan with no hit sends no message
and only describes the checked ray.

Use `assist send <recipient relay ID>` to send the current ship position
as a one-time assistance request to a wingmate over Galnet. The receiver
shows it above other Galnet marks with an `[ASSIST]` label. It expires
30 seconds after sending, including time spent in a relay queue. ShipLink
shows each mark's age from the time of sending. Update the paired Galnet
relay with ShipLink so it can apply the same deadline to its retry queue. When the mark list is full,
an assistance request can replace the oldest ordinary mark. The
existing `assist on` and `assist off` commands control the radio heartbeat.

### Flight control PB

`apps/fcs` is an opt-in cockpit flight controller. Copy the contents of
`build/output/apps/fcs/fcs/FlightControl.pb.cs` into a Programmable Block on the
same grid as the ship's thrusters and gyros. Name the occupied cockpit with
`[FCS]`. Disable competing remote autopilots and clear existing overrides before
engaging. The controller starts off after compilation or reload.

- `normal` enables assisted flight with six-direction thrust, a 10% thrust
  margin when possible, and support and braking checks. The default braking
  budget is 50 m per local axis. Set a different budget with `brake <metres>`
  (1–500). FCS keeps the selected budget across recompile and restart. The
  value is a flight model input. FCS does not measure the distance to terrain.
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
  settings. Leaving the cockpit or enabling a remote autopilot also releases
  control while the PB is running. If an actuator is unavailable during
  release, FCS attempts the remaining settings and reports a warning.

Name an LCD on the FCS block's grid exactly `[FCS STATUS]` and run `status`
to show live flight status there. The panel refreshes while FCS is active
and shows the reason when FCS releases control.

The FCS status shows speed and the highest stopping-distance estimate across
the cockpit's local X, Y and Z axes alongside the selected braking budget.
The estimate assumes current orientation, effective thrust and estimated
external acceleration on that axis. In-game trials should compare the
estimate with measured stopping distance at several loads and orientations.

For a fixed berth, name the ship's connector `[FCS DOCK]`. While connected
in the desired orientation, run `dock record` to save its world pose.

FCS saves up to eight recorded station grids, with one berth per grid.
Use `dock list` to find their grid IDs and `dock select <grid ID>` before
approaching a previously recorded station. Finish any active docking or
charge and departure sequence before switching.

Run `dock guide` on the next approach to show the position correction along the
recorded connector's right, up and forward axes, plus forward and up
alignment errors. Use `dock off` to hide the guide. The guide provides
readouts during FCS flight. The pilot flies the approach and connects using
the connector controls. Re-record the pose if the berth moves.

For a supervised final approach, turn off the standard autopilot, occupy the
`[FCS]` cockpit, and line up within 3 m of the recorded pose with at most 0.4 m
of side or vertical error and five degrees of angular error. Run
`dock approach`. FCS targets up to 0.35 m/s and calls Connect when the matching
port is connectable and ship speed is low enough. Use a clear approach
path. Pilot movement or rotation input, `dock off`, loss of control, leaving
the corridor or a timeout releases FCS. Record the berth again after this
update so the saved pose includes the port ID.

For a first uncrewed test, name exactly one working cockpit or remote
control on the drone grid `[FCS DRONE]`. Use the same recorded berth and
alignment limits. Once the existing mining script has brought the drone
into the final corridor and disabled its remote autopilot, it can run the
FCS block with `dock drone`. This mode starts from FCS off with no player
controlling the grid. It releases control if a player takes over or the
remote autopilot restarts. It covers the final approach only.

To hand control back to the mining workflow, optionally add timer blocks
named `[FCS DOCK OK]` and `[FCS DOCK FAIL]` on the drone grid. FCS starts
the success timer after connecting to the recorded port and restoring the
controls. A refused or aborted uncrewed approach starts the failure timer.
Give each timer a short delay. Set the success timer to run the unload stage
and the failure timer to notify
the operator or hold the workflow. Reissuing `dock drone` while an approach
is running leaves the in-flight attempt alone.

After a successful uncrewed docking, FCS sets the batteries on the drone
grid to Recharge before starting the success timer. Use `dock depart` when
the mining workflow is ready to leave. FCS sets those batteries to Auto
before requesting disconnect. If the connector releases separately while the programmable block stays
powered, FCS sets the batteries to Auto on its next power check. The
charging state survives a programmable-block recompile. While still
connected to the recorded port, the power check restores Recharge after
a failed or changed battery setting. A saved disconnect continues with
Auto. If the connected port changes, FCS restores Auto and clears the old
departure request. Verify both charge modes at the drone terminal with
the base powered.

The uncrewed charge cycle defaults to a 95% departure threshold, measured
across enabled, functional ship batteries by stored energy. Set it with
`dock charge <0–100>`. A `dock depart` request below the threshold is saved
and retried after charging. `dock off` cancels the pending departure while
leaving the drone docked. The mining workflow should request departure after
unloading. An undock outside FCS bypasses the charge threshold.

An optional timer block named `[FCS DEPART OK]` on the drone grid starts
after a requested departure is confirmed and the batteries are in Auto.
Use its action to start the outbound route. This signal is reserved for
FCS-requested departures, including requests that waited for charging.

To investigate an automatic approach that stalls near the port, record a
successful connected pose and compare the guide readings at the stall with
the same ship, berth and cargo load in a copy of the world without the
physics mods. Capture the connector state, position and alignment errors,
and which controller was active. Keep the guide in readout-only use during
the comparison.

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

Cancelled tasks leave the live queue immediately. The broker keeps the 500
most recent cancellation IDs and 500 completion IDs as separate replay
histories in Storage. A duplicate outside that history may be accepted as a
new request.

The queue provides at-least-once assignment, producer-side request deduplication, and consumer-side durable checkpoints. It cannot guarantee exactly-once physical work: if a PB loses power after performing a world action but before persisting its checkpoint, the action may be attempted again. Physical handlers must therefore verify world state and be idempotent. Lease epochs fence stale messages at the broker, but cannot undo an action already started in the game.

## Refactoring Notes

- All `Program` classes previously duplicated the same helper methods (`TryParseEnvelope`, `TryGetJsonProperty`, `TryReadJsonString`, `ToJsonString`, `EncodeStorage`, `DecodeStorage`). These now live in `libs/igc-core/TaskBusIgcHelper.cs` and are called via `TaskBusIgcHelper.<method>()`.
- `Logger`, `Autopilot`, and `RemoteControlPhysicalInfo` are now `public` with null-safe constructors.
- `AMN.Base` scans inventory from all functional block types with inventories, not just cargo containers.
- Legacy root-level `.csproj` files (`TaskBus.Core.csproj`, `TaskBroker.csproj`, `TaskConsumer.csproj`, `SpaceEngineers.csproj`) are retained for backward compatibility but the MDK² structure under `libs/`, `services/`, `apps/` is the canonical layout going forward.
