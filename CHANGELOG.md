# Astraeus

## 1.0.2

### Safety Improvements

- **Stopping the mount.** A stop now waits for the mount to confirm that tracking is off as well as
  the slew, and asks again if it hasn't. A mount that keeps tracking after two requests is logged
  with "Check the mount" instead of "Tracking stopped".
- **Securing the observatory.** The session-end email only reports the observatory as secured when
  a roof the Autopilot works reads closed. A dome that is not connected leaves the roof's state
  unknown, and that is now reported as not secured whether or not the Autopilot opened the roof
  that night. It is checked again when the email goes, so a mount or dome that drops off, or a roof
  or mount moved by hand after the secure, is no longer reported as secured. If the dome drops
  offline after the Autopilot opened the roof, the secure also raises a critical alert. Switching
  the Autopilot off names a roof whose state is unknown in its warning.
- **Mount limits.** With limits on, the Autopilot now watches them in every state where the mount
  tracks a target, including the waits between frames and while it talks to the server. A breach
  stops, parks, closes and switches off, as it did during exposures.
- **Roof watchdog.** When the Autopilot does not operate the roof, the watchdog keeps alerting on a
  roof left open in unsafe conditions while the Autopilot waits for safe weather. Before, it stood
  down for the wait.
- **Dashboard commands during the Autopilot.** Aborting the camera from the dashboard while the
  Autopilot runs is refused, with a message saying to switch the Autopilot off first. A rotator or
  camera command that interrupts an Autopilot step is treated as a failed frame, which is reported
  and retried, instead of ending the night with a park and a roof close.
- **Closing N.I.N.A.** Closing N.I.N.A. during an Autopilot exposure stops the exposure, guiding and
  tracking and leaves the roof as found. Before, the camera could be torn down under the exposure,
  which the Autopilot read as a fault and answered with a park and a roof close. The stop now halts
  the hardware before it reports to the server, so a slow connection cannot hold it up.
- **Tracking modes.** A tracking mode the plugin does not know is refused with a reason. Before, it
  was read as Sidereal and started the mount tracking.
- **Checked values.** Dashboard commands and settings from the server are now checked against
  sensible ranges before they reach a device or the N.I.N.A. profile: a declination beyond ±90°, a
  rotator angle beyond 360°, a binning of 0 or a latitude beyond ±90° are refused with a reason. A
  settings update with one such value is rejected whole and the current values are sent back.
- **Equipment that won't connect.** If the Autopilot is set to connect equipment and a device with a
  default does not connect when the night starts, it sends a critical alert naming the device, then
  either waits for it to be connected by hand (safety monitor, mount, camera, dome, flat panel) or
  carries on without it.
- **Mount position lost.** With limits on, a mount that stops reporting its position while the
  Autopilot is using it, or drops off while it was moving, is now treated as a limit breach once it
  has been out of reach for 30 seconds: stop, park, close and switch off, with the reason in the
  session-end email. Shorter drop-outs are logged and the night carries on. A watcher whose check
  keeps failing now does the same instead of retrying silently. Outside the Autopilot, the same
  30 seconds stops the mount and sends a critical alert, or only the alert if it is not connected.
- **Parking only under an open roof.** A secure now parks only when the roof reads open, and stops
  the park if the roof stops reading open part-way. Otherwise the mount is stopped where it is and a
  critical alert says why. If the roof reads open again straight after, as when the dome drops off
  for a moment, it tries the park once more. Turn on "Mount can move under closed roof" if your
  park position is safe with the roof closed. Before, the unsafe secure could park while a roof
  controller was already closing the roof. A park that N.I.N.A. finishes itself before closing on
  unsafe now counts as parked.
- **Roof watching.** The Autopilot now watches the roof in every step of the night, not only the
  steps that also watch the safety monitor, so a roof that closes while it uploads, checks the next
  target or waits between frames is caught within seconds. A roof that closes because conditions
  turned unsafe takes the usual unsafe path.
- **A roof the Autopilot doesn't operate closing mid-night.** With "Wait for roof to open", a roof
  that closes while the Autopilot images is now handled like unsafe conditions: the mount is parked
  if "Mount can move under closed roof" is on, otherwise stopped where it is with an alert, and the
  Autopilot waits for safe conditions and the roof to open again. With "Operate roof automatically",
  a roof that closes on its own still stops the mount and switches the Autopilot off.
- **"Park mount to open roof" is now "Mount can move under closed roof".** Same setting, and your
  choice carries over. Besides parking so N.I.N.A. will open the roof, it now also lets a secure
  park under a roof that is closing or closed. Off by default. Leave it off unless your park
  position is always safe to reach with the roof closed.
- **"Ignore roof" is now "No roof".** Same setting, and your choice carries over. The Autopilot never
  checks a roof in this mode, so it unparks, slews, images and parks as if the sky were always open.
  Choose it only for a pier with nothing overhead. If anything can close over the telescope, use
  "Wait for roof to open". The roof handling list now has a tooltip explaining each mode.

### Bug Fixes

- **Mount site prompt.** If N.I.N.A.'s "Synchronize site settings" prompt holds up the mount's
  connect, the Autopilot names it in the log and on the dashboard and waits for someone to answer
  it in N.I.N.A., instead of timing out with no reason given. The prompt is never answered for you.
- **Manual filter wheel and rotator.** The Autopilot no longer uses N.I.N.A.'s Manual Filter Wheel or
  Manual Rotator, which wait for someone at the PC on every change. If connected when the night
  starts it is disconnected with a warning, and the default device is connected instead. A manual
  default is skipped like "None", so frames that need a filter are refused instead of stopping the
  night at the first filter change.
- **Sign-in warnings.** After a sign-out, N.I.N.A.'s log no longer repeats "Failed to read setting
  'Astraeus.RefreshToken'" every few seconds.
- **Clearer log lines.** At dawn, a mount that was never connected is logged as not connected,
  instead of "Park call failed after 0.0s". Switching the Autopilot off names only what is actually
  left unsecured, and no longer sends a stop to a mount that is already parked. A flat panel that
  is not connected no longer reads "Cover disconnected".
- **A bad mount reading.** A mount that briefly reports no position, or an invalid one, is no
  longer read as past the meridian limit. Before, a single such reading could stop the mount, or
  during the Autopilot park it, close the roof and switch the Autopilot off.
- **Stopping a park part-way.** When the Autopilot has to stop a park before it finishes, it now
  always sends the mount a stop. Before, a driver that doesn't report "slewing" while it parks
  carried on to the park position.
- **A slew is judged on arrival.** Outside the Autopilot, the mount limit backstop no longer stops a
  slew part-way on a low reading. The slew's target was checked before it started, and where the
  driver takes the mount on the way is the driver's concern. The mount is judged once the slew ends.
- **False alarm on N.I.N.A. connect.** No more critical alert 30 seconds after starting N.I.N.A.
  with the mount not yet connected. The backstop only reports a mount it has seen moving, so a
  mount never connected since N.I.N.A. started is not counted as lost. A mount that drops off
  while tracking still gets the alert.

### Tests

- **The plugin now ships with its test suite.** `tests/Astraeus.Tests` is an NUnit project of
  around 650 tests that runs without N.I.N.A.: the plugin is built over mocked N.I.N.A. services and
  N.I.N.A.'s own settings.
- **Test Coverage Expansion.** Over 300 new tests were added to cover an extensive range of new scenarios where as previously the tests covered mostly HTTP/Websocket messaging. In order to do this the following changes to the plugin source were required.
  - **34 private helpers made `internal`** 
  - **Alerts and log lines made observable**. The observatory raises an `internal` event
    before the sign-in check that drops alerts when signed out, and before the socket check that
    drops dashboard lines, so a test sees what a night would have emailed and logged.
  - **A clock on the observatory** . Every wait, timeout and stopwatch on the Autopilot's
    safety path reads one `TimeProvider` instead of the system time. In production it is the system
    clock; in a test it is a fake, so a whole night runs in milliseconds.
  - **The server behind a handler**. The web client accepts an inner HTTP handler, so a
    test can answer the plugin's requests and read what it sent.
  - **The mount's sidereal time as a seam**, since N.I.N.A.'s own calls a native library
    the test machine does not have.

### Policy Changes

- **Cloud Upload** - The readme.md now explicit reads that cloud uploads are on by default.

## 1.0.1

First public beta, through N.I.N.A.'s plugin manager.

- **Install from N.I.N.A.** Astraeus is listed in N.I.N.A.'s beta plugin channel, so it can be
  installed and updated from **Plugins > Available**. The setup file installs to the same folder,
  so the two replace each other.
- **N.I.N.A. 3.2.** The plugin now requires N.I.N.A. 3.2 or later.
- **Server shown on the options page.** The Account section names the server the plugin talks to.
- **Update notices.** The plugin tells the server its version. If a future server stops supporting
  this version, the plugin says so in N.I.N.A., on the options page and in the log, and stops
  talking to the server instead of failing over and over. A running Autopilot parks the mount,
  closes the roof and switches off.
- **Roof safety.** The roof can open without a safety monitor when N.I.N.A.'s settings allow it. 
- **Calibration masters.** Masters generated on this PC are found again after uploading, so frames
  are calibrated with them. Before, every master was skipped as "not generated on this PC". A
  regenerated master replaces the older file. Calibration frames without GAIN/OFFSET headers now
  give one warning for the whole scan instead of one per file.
- **Autopilot.** A target skipped because setup took longer than the server allows is logged as a
  warning saying how long setup took, not as an error. The server schedules it again.
- **Autofocus.** A run N.I.N.A. cannot complete is reported, so the server waits before asking for
  another. Smart Autofocus skips moves smaller than a tenth of N.I.N.A.'s autofocus step.
- **Default devices.** The options page no longer forgets a default device when N.I.N.A. renames
  it during a rescan.
- **Dawn.** The camera is warmed and disconnected properly at dawn, and devices with no default
  device are left connected, since they couldn't be reconnected at dusk.
- **Guiding.** Guiding is stopped before hub slews, parks, homes and limit stops, and before a
  focus move when N.I.N.A.'s settings ask for it, and is resumed after a hub slew.
- **Uploads.** A report the server answers with a malformed calibration plan still uploads the
  frame, and a rotated sign-in is kept even if the server's reply is partly malformed.
- **Log lines** are plainer, and the plugin's own lines name files, never folders.
- **Licences.** The full LGPL 2.1 and GPL 2 texts for the bundled LibVLC now ship with the plugin.

## 1.0.0

First release, to a closed group of testers.

- **Dashboard connection.** Sign in to Astraeus from the plugin's options page through your browser.
  The plugin keeps a connection to the server and shows the observatory on the dashboard at
  CosmicVaults.com.
- **Equipment status and control.** Camera, mount, dome or roof, filter wheel, focuser, rotator,
  flat panel, guider, safety monitor, weather station and switch hub report their state live, and
  most can be operated from the dashboard.
- **Control Hub captures.** Take frames from the dashboard with a preview of each one. Saved frames
  can be plate-solved, calibrated and uploaded.
- **Autopilot.** The server chooses the targets and the plugin runs the night:
  - It waits for dusk (sunset, nautical or astronomical), connects the equipment or waits for you to,
    and cools the camera.
  - It opens the roof, or waits for it to be opened, then slews, centres and rotates to each target.
  - It focuses, guides and dithers, captures and reports each frame, and at dawn secures the
    observatory and warms the camera.
  - It runs your own N.I.N.A. sequences at start-up and shutdown if you set them.
- **Safety.** An unsafe reading, a roof that closes or a mount limit being reached stops or secures
  the observatory. A roof left open when it is unsafe or after dawn raises an alert even when the
  Autopilot is off. A flat panel the Autopilot handles must be connected before the night starts,
  and frames that show no stars (a closed cover or dust cap, or cloud) raise an alert. Critical
  problems are emailed to the observatory's observers.
- **Mount limits and meridian flips.** Minimum altitude, your N.I.N.A. custom horizon and N.I.N.A.'s
  meridian flip settings are enforced for the Autopilot, for Control Hub captures and while the mount
  is idle. A German equatorial mount is flipped instead of stopped when meridian flips are on.
- **Autofocus.** Refocus on a time interval, a temperature change or a filter change. Or let the
  server decide with Smart Autofocus, which also moves the focuser to its recommended position
  between runs.
- **Cloud upload.** Full frames go to your cloud storage in the background, after a check that
  there is room.
- **Calibration.** The "Generate & Upload Calibration Masters" sequence instruction stacks your
  darks, biases and flats into masters and uploads them. Frames are calibrated on this PC with those
  masters, and otherwise left uncalibrated.
- **Settings sync.** N.I.N.A.'s site, telescope, camera, filter wheel, focuser, dome, Alpaca, image
  file and meridian flip settings, and the Autopilot options, can be viewed and changed from the
  dashboard. File paths are never synced.
- **Webcam feed.** An all-sky or observatory camera, over HTTP snapshots, MJPEG or RTSP, shown on the
  dashboard while somebody is watching.
- **N.I.N.A.'s log** is shown on the dashboard.
- **File name patterns** `$$ASTRAEUSPROJECTNAME$$` and `$$ASTRAEUSTARGETNAME$$`.
- **Installer** for the current Windows user, with no administrator rights needed.
