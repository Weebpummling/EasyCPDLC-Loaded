# VNS430 MSFS 2024 keybind bridge

This folder is the simulator-side input path for the desktop VNS430 panel. It
lets cockpit hardware drive the panel; there is **no in-simulator 3D
instrument**. The desktop panel is the display.

It contains:

- the standalone WASM bridge (`Bridge/`);
- the VNS430 and DCDU MobiFlight profiles (`MobiFlight/`).

The bridge is optional. VNS430 works from the mouse and the desktop panel with
nothing installed here. Install this module only when you want physical buttons
and encoders to drive the panel through MobiFlight.

## How input flows

```text
physical button -> MobiFlight -> L:EASYCPDLC_VNS_COMMAND -> WASM bridge
    -> SimConnect Client Data (EasyCPDLC.VNS430.Command.v1) -> desktop panel
```

Status flows back the same way, so the module can light hardware annunciators:

- `L:EASYCPDLC_VNS_MODULE_ALIVE`
- `L:EASYCPDLC_VNS_APP_CONNECTED`
- `L:EASYCPDLC_VNS_VATSIM_CONNECTED`
- `L:EASYCPDLC_VNS_UNREAD_COUNT`
- `L:EASYCPDLC_VNS_PAGE`
- `L:EASYCPDLC_VNS_CURSOR_ACTIVE`
- `L:EASYCPDLC_DCDU_MODE`

The bridge maps no Garmin, GPS, radio, CDI, flight-plan, aircraft-key, or
InputEvent commands. It only carries the private `EASYCPDLC_VNS_*` /
`EASYCPDLC_DCDU_*` variables.

## What you need

| Component | Needed for | Notes |
|---|---|---|
| MSFS 2024 | running the bridge | The bridge is an MSFS standalone WASM module. |
| MobiFlight Connector | sending button presses | Free. Reads and writes the `L:` variables above. |
| EasyCPDLC Print + eLC with the VNS430 panel | the display | Start it with `EasyCPDLC.exe --vns430`, or the tray **Open VNS430 panel**. |
| MSFS 2024 SDK | **building** the bridge only | Not needed on the flying PC. See the note under *Install the WASM bridge*. |

There is no prebuilt `.wasm` in this repository. The package is produced by the
build step below. You can build it once on a machine that has the SDK and copy
the finished folder to any flying PC; the SDK is not required to run it.

## Install the WASM bridge

### 1. Build the module package

On a machine with the MSFS 2024 SDK installed at its default location
(`C:\MSFS 2024 SDK`):

```powershell
.\Bridge\Build-Wasm.ps1
```

If the SDK lives elsewhere, pass its root:

```powershell
.\Bridge\Build-Wasm.ps1 -SdkRoot "D:\MSFS 2024 SDK"
```

The script compiles `Sources\EasyCpdlcVnsModule.cpp`, links the WASM module, and
writes a complete, ready-to-install package to:

```text
Bridge\BuiltPackage\easycpdlc-vns430-bridge\
    manifest.json
    layout.json
    modules\easycpdlc-vns430-bridge.wasm
```

It prints the WASM byte count and SHA-256 when it finishes. If it stops with
`Required SDK/source file not found`, the `-SdkRoot` path is wrong or the SDK's
WASM toolchain is not installed.

### 2. Copy the package into the Community folder

Copy the whole **`easycpdlc-vns430-bridge`** folder (not just the `.wasm`) into
your MSFS 2024 Community folder. Typical locations:

```text
Microsoft Store:  %LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community
Steam:            %APPDATA%\Microsoft Flight Simulator 2024\Packages\Community
```

If neither exists, open `UserCfg.opt` for MSFS 2024, read the
`InstalledPackagesPath` line, and use the `Community` folder beneath that path.
The result should be:

```text
...\Community\easycpdlc-vns430-bridge\manifest.json
...\Community\easycpdlc-vns430-bridge\layout.json
...\Community\easycpdlc-vns430-bridge\modules\easycpdlc-vns430-bridge.wasm
```

### 3. Restart and verify

1. Start MSFS 2024 and load into any flight.
2. Start EasyCPDLC and open the VNS430 panel.
3. Open MobiFlight Connector and watch `L:EASYCPDLC_VNS_MODULE_ALIVE`. It ticks
   to `1` about once a second while the module is loaded.
4. When the desktop app is running and reachable, `L:EASYCPDLC_VNS_APP_CONNECTED`
   reads `1`.

If `MODULE_ALIVE` never moves, the package is in the wrong folder or the sim was
not restarted after copying it. If `MODULE_ALIVE` is `1` but `APP_CONNECTED`
stays `0`, the bridge is loaded but the desktop panel is not running.

The bridge uses named SimConnect Client Data. Nothing else should register these
names:

```text
EasyCPDLC.VNS430.Command.v1
EasyCPDLC.VNS430.Status.v1
```

## Install and use the MobiFlight profiles

The profiles turn a physical board into the VNS430 keys. Import one of:

- `MobiFlight\EasyCPDLC-VNS430-Module.mfproj` for the VNS430 dual encoder and
  face buttons (`L:EASYCPDLC_VNS_COMMAND` values `1` through `18`).
- `MobiFlight\EasyCPDLC-DCDU-Module.mfproj` for twelve DCDU LSKs plus connect,
  AOC, ATC, settings, reload, print, reprint, and hide.
- `MobiFlight\EasyCPDLC-WinWing-737-CDU.mfproj` for the LSK-only **CDU display
  mode**: the twelve LSKs plus the full **Boeing 737NG** CDU keypad (A-Z, 0-9,
  `SP`/`DEL`/`CLR`/`/`/`.`/`+/-`, and the function keys `INIT REF`/`RTE`/`CLB`/
  `CRZ`/`DES`/`MENU`/`LEGS`/`DEP ARR`/`HOLD`/`PROG`/`EXEC`/`N1 LIMIT`/`FIX`/
  `PREV`/`NEXT PAGE`/`BRT`). Each key is one momentary `L:EASYCPDLC_CDU_*` input
  (`EASYCPDLC_CDU_A` … `_Z`, `_0` … `_9`, `_SP`, `_CLB`, `_EXEC`, …), so a
  **WinWing 737 CDU** can be bound directly: reassign the placeholder controller
  to your WinWing device and map its keys, exactly like other aircraft's WinWing
  CDU profiles. These keys are gated by the same DCDU-mode flag and require the
  CDU display style (`SETUP > DCDU STYLE > CDU`, or `DcduStyle.txt = CDU`).

### 1. Open the profile

In MobiFlight Connector, use **File > Open** and select the `.mfproj` file. Its
rows appear on the **Input** tab. Each row is one physical control mapped to one
`L:EASYCPDLC_VNS_COMMAND` value.

### 2. Point the profile at your real board

Both profiles ship bound to a placeholder controller so they import cleanly on
any machine:

```text
Controller: EasyCPDLC VNS430 Template
Serial:     EASYCPDLC-VNS430-TEMPLATE
```

That placeholder matches no real hardware, so every row shows as unassigned
until you reassign it. For each row on the Input tab, change the device from the
template to your connected MobiFlight board (or joystick), then pick the actual
pin or button that should fire that command. Leave the **command** side of each
row unchanged; that is the private VNS430 binding and is already correct.

Save the project once the rows point at real pins.

### 3. Command reference (VNS430 profile)

The command values match the panel keys documented in the
[VNS430 README](../README.md):

| Command | Control | Panel action |
|---:|---|---|
| 1 / 2 | Large encoder left / right | Move selection, or change page group |
| 3 / 4 | Small encoder left / right | Change page within the group |
| 5 | Encoder push | Toggle the cursor |
| 6 | `ENT` | Activate the selection |
| 7 | `CLR` | Back or clear |
| 8 | `MENU` | Menu overlay |
| 9 | `MSG` | Messages, unread first |
| 10 | `FPL` | ATC request menu |
| 11 | `PROC` | AOC / telex menu |
| 12 | `D→` | Hoppie logon page |
| 13 | `OBS` | Toggle the cursor |
| 14 | `CDI` | Connect or disconnect VATSIM |
| 15 / 16 | `RNG +` / `RNG −` | Larger / smaller LCD text |
| 17 | `VLOC` | Message log, every message |
| 18 | Power | Show or hide the panel window |

Value `18` (`EASYCPDLC_VNS_POWER`) is handy on a single hardware key to bring the
panel up and put it away.

### 4. VNS430 mode versus DCDU mode

VNS430 mode uses `L:EASYCPDLC_VNS_COMMAND` values `1` through `18`. The tray
option **Use MSFS module for DCDU controls** gates the separate momentary
`L:EASYCPDLC_DCDU_*` inputs used by the DCDU profile.

- With the tray option off, VNS430 commands are active and the DCDU inputs are
  cleared and ignored.
- With the tray option on, the DCDU inputs drive the twelve LSKs and function
  keys, and VNS430 commands are ignored.

Import the profile that matches the mode you fly in. Do not run both profiles
against the same buttons at once.

### 5. Drive annunciators from status (optional)

To light lamps or displays on the board, add MobiFlight output configs that read
the status variables listed under *How input flows*. For example,
`L:EASYCPDLC_VNS_UNREAD_COUNT` drives an unread-message indicator, and
`L:EASYCPDLC_VNS_APP_CONNECTED` confirms the desktop panel is up. These are
optional; the profiles above only cover input.

## Using the panel as a screen

With hardware driving input, run the desktop panel as a bare LCD behind the
physical unit: right-click the tray icon and turn off **artwork** and
**interactive zones**. See the [VNS430 README](../README.md).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `MODULE_ALIVE` never reaches 1 | Package not in the Community folder, folder structure wrong, or sim not restarted after copying. |
| `MODULE_ALIVE` is 1, `APP_CONNECTED` is 0 | Bridge loaded but the desktop VNS430 panel is not running. Start EasyCPDLC and open the panel. |
| Buttons do nothing, L-vars do change | Wrong mode: check the tray **Use MSFS module for DCDU controls** option against the profile you imported. |
| Buttons do nothing, L-vars do not change | Profile rows still bound to the template controller. Reassign each row to your board and pins. |
| `Build-Wasm.ps1` reports a missing SDK file | `-SdkRoot` path is wrong, or the SDK's WASM toolchain is not installed. |

This module is not an aircraft ACARS adapter. Hoppie and future aircraft-inbox
routing remain documented in
[`docs/HOPPIE-AIRCRAFT-ACARS-ROUTING.md`](../../../docs/HOPPIE-AIRCRAFT-ACARS-ROUTING.md).
