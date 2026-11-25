# Radar

Load the Radar programmable block script and place cameras in the
`Radar Cameras` block group or give them the `[RADAR CAM]` name prefix.
Run `on` to begin scanning and `off` to stop. Use `unlock` to drop the
current contact and resume searching. A local `[RADAR LCD]` shows the
same status as the programmable block. `range <meters>` sets the maximum
raycast range for the current session.

- **Fixed** cycles through the configured cameras, scans along their
  forward axes and follows the observed entity while a camera can scan
  its predicted location.
- **Gimbal** expects an azimuth rotor on the programmable block grid, an
  elevation hinge on the rotor head grid and a camera on the hinge head
  grid. Default block names are `[RADAR AZIMUTH]` and `[RADAR ELEVATION]`.
  The radar searches in five-degree azimuth steps at the current elevation
  and waits at each step until a camera can raycast. After acquisition it
  follows the contact with both motors. `off` stops both motors.

Use `mode fixed` or `mode gimbal` to switch in the current session.
Set `Mode=Fixed` or `Mode=Gimbal` under `[Radar]` in Custom Data to
choose the startup mode. Configure names, travel limits, maximum speed
and axis inversion under `[Gimbal]`. Set a search range that allows
the cameras to recharge between rays. Use `status` to distinguish a
measured lock from a predicted contact during a missed measurement. The
display shows the entity ID, type, relationship, age, range and speed
from the last raycast. The `GPS:Radar last fix:` line shows the measured
coordinates and remains available after tracking times out. Use
`unlock` to clear the observation.

To calibrate the park position, run `off` in Gimbal mode, position the
two axes manually, stop their manual movement, then run `gimbal calibrate`.
The command records both angles for the current session and prints
`ParkAzimuthDegrees` and `ParkElevationDegrees`. Copy those settings to
`[Gimbal]` in Custom Data to retain them across reloads.

Run `gimbal park` to stop scanning and move both axes to their park
positions. The default for each axis is its hardware zero. A setpoint
outside the allowed travel is clamped to the nearest limit. The display shows `PARKING` during movement and
`PARKED` once both axes reach their setpoints. Use `off` to stop movement
or `scan` to resume scanning. Parking stops both drives if either axis
fails to make progress for five seconds. The same timeout stops Gimbal
search when azimuth stalls between scan positions. Run `scan` to retry.

## Sharing a measured contact

Configure the local ShipLink PB and Galnet relay as described in
[the fleet guide](../TaskBus.md#fleet-display-for-space-engineers-combat).
Radar looks for a local PB named `[GALNET APP]` by default. Set
`ShipLinkBlock` under `[Radar]` if the local ShipLink uses another name.
Run `share <recipient relay ID>` on Radar within two seconds of a raycast
measurement. ShipLink sends a contact named `Radar <entity ID>` through
the existing fleet channel. Check ShipLink output and the recipient's
display to confirm delivery.

## Sharing a turret observation

Name a local large turret `[SHIPLINK TURRET]`. With a contact already
selected by the turret, run `contact turret <recipient relay ID>` on
ShipLink. The report includes its entity ID, position, velocity and
observation time. Select it at the receiver as `Turret <entity ID>`.

## Sharing a cockpit sighting

Install a forward camera named `[SHIPLINK COCKPIT CAM]` on the cockpit
grid and align it with the cockpit. With the cockpit occupied, point the
ship at the object and run `contact cockpit <recipient relay ID> <range>`
on ShipLink. Range must be between 1 and 5000 metres. The camera must
have enough raycast charge. Select the report as `Cockpit <entity ID>`.

## Acquiring a fleet mark

Name the receiving Radar PB `[RADAR]`. In ShipLink, select a fresh
mark with `contact track <sender relay ID> Radar <entity ID>` (or use
`Turret <entity ID>` or `Cockpit <entity ID>` for those observations) and run
`contact acquire`. Fixed Radar attempts a raycast when the mark is within
a camera's field of view. Gimbal also points toward the reported position. New Radar marks include
velocity and measurement time, so the acquisition ray follows the
predicted position while the cue is current. Older marks use their GPS.
The radar starts tracking only after a local raycast confirms the same
entity ID. An expired cue stops the attempt.

## Diagnostics and acceptance

If the status is `UNAVAILABLE`, follow the message about camera placement,
the rotor and hinge connection, motor power and locks, or incompatible
travel limits. Run `on` after replacing or renaming a block to rescan the grid.

Verify these scenarios in Space Engineers before recording an acceptance result.

1. In Fixed, scan a stationary object, then move it while it is in the
   camera field of view. Confirm that repeated measurements retain its entity ID.
2. Move the object out of range. Confirm that tracking expires and that
   the LCD shows its last measured position and increasing age.
3. In Gimbal, use a bounded azimuth sector. Confirm pauses at scan positions,
   reversal at both ends, and tracking movement on both axes. Confirm that
   the configured inversion matches the mounting of each motor.
4. Disable or detach a camera, rotor or hinge while scanning. Confirm the
   reported hardware fault, the last measured position and zero velocity
   setpoints on both available axes. Restore the blocks and run `on` if
   they were replaced or raycast was disabled. Confirm that Radar starts
   a fresh search after recovery.
5. Run `off` while tracking and confirm both axes stop. Repeat when switching
   from Gimbal to Fixed.
6. Park from a nonzero position, first with both zero angles reachable
   and then with zero outside one axis's limits. Confirm the final
   setpoints and zero drive velocity. Repeat and interrupt with `off`.
