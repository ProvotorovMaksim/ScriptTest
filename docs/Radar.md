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
display shows the age of the last measurement and preserves the last
known coordinates after tracking times out. Use `unlock` to clear them.

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
   reported hardware fault and zero velocity setpoints on both available axes.
   Restore the blocks and run `on` if they were replaced.
5. Run `off` while tracking and confirm both axes stop. Repeat when switching
   from Gimbal to Fixed.
