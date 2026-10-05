# Astraeus

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
