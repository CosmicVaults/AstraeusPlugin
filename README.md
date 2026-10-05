# Astraeus

Astraeus is a plugin for [N.I.N.A.](https://nighttime-imaging.eu/) 3.2 by
[Cosmic Vaults](https://cosmicvaults.com). It connects N.I.N.A. to the Astraeus dashboard, so you
can watch and run your observatory from a browser and leave the Autopilot to image through the
night. What each release changed is in [CHANGELOG.md](CHANGELOG.md).

You need a Cosmic Vaults account to use it.

Requirements: N.I.N.A. 3.2 or later, 64-bit, on Windows 10 or later.

## Installing from N.I.N.A.'s plugin manager

Astraeus is in public beta, so it is listed in N.I.N.A.'s beta plugin channel:

1. In N.I.N.A., open **Options > General**, find **Plugin Repositories** and add
   `https://nighttime-imaging.eu/wp-json/nina/v1/beta`.
2. Open **Plugins > Available**, select **Astraeus** and click **Install**.
3. Restart N.I.N.A.
4. Open **Plugins > Installed > Astraeus**. In the **Account** section click **Sign in with
   browser** and sign in to your Cosmic Vaults account in the browser window that opens.

Sign-in hands the browser back to the plugin through a port on this PC between 47821 and 47830
(on 127.0.0.1 only). A firewall that blocks local connections to those ports will stop sign-in
from completing.

Updates appear in **Plugins > Installed** like any other plugin's.

## Installing with the setup file

1. Download `Astraeus-Setup-<version>.exe` from [cosmicvaults.com](https://cosmicvaults.com) or
   from this repository's [releases](https://github.com/CosmicVaults/AstraeusPlugin/releases).
2. Run it. It installs for the current Windows user only and does not ask for administrator
   rights.

   The setup file is not yet code-signed, so the first time you run it Windows may show
   "Windows protected your PC". Click **More info**, then **Run anyway**. To check that what you
   downloaded is what was published, compare its checksum with the one published beside it:

   ```
   certutil -hashfile Astraeus-Setup-<version>.exe SHA256
   ```

3. If N.I.N.A. is open, Setup asks you to close it and waits. Finish or stop whatever it is doing
   first. Setup never closes N.I.N.A. for you.
4. Start N.I.N.A. and sign in as in step 4 above.

The files go to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Astraeus\`, the same folder N.I.N.A.'s plugin
manager uses, so the two ways of installing replace each other rather than adding a second copy.
`3.0.0` is N.I.N.A.'s plugin-API version, not the N.I.N.A. version. It is the same for every
N.I.N.A. 3.x release.

To update, run the newer setup file. It empties the plugin folder and installs afresh, so nothing
from the old version is left behind.

To uninstall, use Windows **Settings > Apps > Installed apps > Astraeus (N.I.N.A. plugin)**, or
N.I.N.A.'s own plugin manager. Close N.I.N.A. first either way.

## Installing by hand

Download `CosmicVaults.NINA.Astraeus.<version>.zip` from the
[releases](https://github.com/CosmicVaults/AstraeusPlugin/releases) and extract it so that
`CosmicVaults.NINA.Astraeus.dll` sits directly in `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Astraeus\`,
with `libvlc\win-x64\` beside it. Then start N.I.N.A.

## Beta

This is a beta. It runs real equipment, so watch the first nights before leaving it unattended,
and keep N.I.N.A.'s own safety settings in place. Please report problems on the
[issues page](https://github.com/CosmicVaults/AstraeusPlugin/issues) with the plugin version,
the N.I.N.A. version and your N.I.N.A. log from `%LOCALAPPDATA%\NINA\Logs`.

## Privacy: what the plugin sends

You sign in through your own browser, so the plugin never sees your Astraeus password. It keeps
only a refresh token, encrypted for your Windows account with DPAPI, in the N.I.N.A. profile.

While the plugin is enabled and signed in, it keeps a connection to the Astraeus server and sends:

- **Equipment status:** connection state, positions, temperatures and similar readings for the
  devices connected in N.I.N.A., including their names, driver names and device IDs. A network
  device's ID can include its address on your local network.
- **N.I.N.A.'s log:** lines at Information level and above, including lines written by other
  plugins, so the dashboard can show what N.I.N.A. is doing. Astraeus's own lines name files, never
  folders. Lines written by N.I.N.A. or other plugins, and error messages from Windows or N.I.N.A.,
  are passed on as written. They can include file and folder paths, such as where N.I.N.A. saved a
  frame. Your Windows profile folder is replaced with `%USERPROFILE%` in every line before it is
  sent. Debug lines stay on this PC.
- **Settings shown on the dashboard:** the site's location (latitude, longitude and elevation),
  and autopilot, equipment, image file and meridian flip options. Folder locations are never sent
  in either direction. N.I.N.A.'s file name patterns are synced.
- **Frames:** a small preview and the statistics of each frame you capture. Full frames, and
  calibrated copies, are uploaded only when Upload to Cloud is on. Captures are identified to the
  server by file name only, not by folder.
- **Calibration masters** you generate with the "Generate & Upload Calibration Masters" instruction,
  together with the name of the N.I.N.A. profile they were made with. Masters are never downloaded.
  A frame is calibrated on this PC only with masters generated here, and otherwise stays
  uncalibrated.
- **Webcam frames,** only while the feed is switched on and someone is watching it on the dashboard.
  The camera's address, username and password never leave this PC.
- **The plugin's version,** on every request, so the server can tell you when an update is needed.

## Building

You need the .NET 8 SDK. For the setup file you also need
[Inno Setup 6.3 or later](https://jrsoftware.org/isdl.php). The build looks for `ISCC.exe` in
`C:\Program Files (x86)\Inno Setup 6` and then in `%LOCALAPPDATA%\Programs\Inno Setup 6`, where an
"install for me only" puts it. If it is somewhere else, pass `-p:InnoSetupCompiler=<path to ISCC.exe>`.

```
dotnet msbuild -t:Installer -p:Configuration=Release
```

This builds Release and stages only the files that ship in `stage\` (the `Package` target). It
then writes `dist\Astraeus-Setup-<version>.exe` and `dist\Astraeus-Setup-<version>.exe.sha256`.

- Release builds talk to the Astraeus server at astraeus.cosmicvaults.com. Debug builds talk to a
  local development server at localhost:8000.
- `<version>` is the four-part Major.Minor.Patch.Build, e.g. `1.0.1.278`. Major.Minor.Patch is
  `AstraeusReleaseVersion` in `Astraeus.csproj`, and Build is the number of days since 2026-01-01.
  `-p:AstraeusFileVersion=<version>` sets the whole number instead, which is what the release
  workflow does with the tag.

Releases are built by [.github/workflows/release.yml](.github/workflows/release.yml) when a version
tag is pushed. The installer script is `installer\Astraeus.iss`. The comment at the top explains
what it does and why it does so little.

## License

Astraeus is licensed under the [Mozilla Public License 2.0](LICENSE.txt). The webcam feed's RTSP
support uses LibVLCSharp and LibVLC, which are distributed with the plugin under their own
licences. See [3rd-party-licenses.txt](3rd-party-licenses.txt) and the [licenses](licenses) folder.

## Security

Please report security problems privately to support@cosmicvaults.com, not as a public issue. See
[SECURITY.md](SECURITY.md).
