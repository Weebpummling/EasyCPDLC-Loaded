# 1 · Installation

Everything you need to get EasyCPDLC-Loaded running, plus the two optional bridges.

**The app itself needs no installer** — extract and run. The optional parts are only
needed if you want physical hardware or vPilot integration.

| Part | Needed for | Time |
|---|---|---|
| [The app](#step-1--install-the-app) | Everything | 1 min |
| [Credentials](#step-2--enter-your-credentials) | Connecting to anything | 2 min |
| [WASM module](#optional-a--msfs-wasm-module) | Physical buttons, encoders, LEDs | 3 min |
| [vPilot bridge](#optional-b--vpilot-bridge) | vTDLS PDCs, Contact Me alerts | 2 min |
| [MobiFlight profiles](#optional-c--mobiflight-profiles) | Binding your hardware | 10 min |

---

## Requirements

- **Windows 11 x64**
- A **VATSIM CID** and **Hoppie ACARS logon code** — [get a Hoppie code here](https://www.hoppie.nl/acars/system/register.html)
- Optional: SimBrief account, eLoadControl API key, SayIntentions API key
- Optional: MSFS 2024 + MobiFlight for hardware
- Optional: a thermal receipt printer

Nothing else. The app ships self-contained — no .NET runtime to install.

---

## Step 1 · Install the app

1. Download the release ZIP.
2. **Extract it somewhere permanent** — `C:\EasyCPDLC-Loaded` or similar.
   Do not run it from inside the ZIP viewer; the app needs to write settings.
3. Run **`EasyCPDLC.exe`**.

That's it. You should see the 737 CDU and a new tray icon.

> **Windows SmartScreen** may warn about an unsigned app. Choose *More info →
> Run anyway*. The release is unsigned because code-signing certificates cost money;
> the SHA-256 of every ZIP is published so you can verify what you downloaded.

---

## Step 2 · Enter your credentials

Right-click the tray icon → **Connection credentials…**

| Field | Required? | Notes |
|---|---|---|
| **VATSIM CID** | For VATSIM | Your numeric ID |
| **Hoppie logon code** | **Yes** | Required for all ACARS/CPDLC traffic |
| **SimBrief username / ID** | Optional | Flight plan + loadsheet data |
| **eLoadControl API key** | Optional | Loadsheet generation |
| **SayIntentions API key** | Optional | SayIntentions network + weather |

You can also enter these on the CDU itself: `SETUP` → `<ACCOUNT`.

Credentials are encrypted for your Windows account (DPAPI) and never written to logs.

> If something required is missing, the `<SETUP` item on the CDU menu turns **amber**
> so you can see it at a glance.

---

## ⚠️ Step 3 · Turn off your aircraft's Hoppie

**Do this before you connect.** If your aircraft has built-in Hoppie/ACARS support
(PMDG, Fenix, iniBuilds, FSLabs, ToLiss…), set its network to **NONE** and clear its
logon code.

Hoppie delivers each message **once**, to whichever client asks for it first. Two
clients on the same callsign means your messages get split unpredictably — some land
in the aircraft, some here, and neither side shows the whole conversation.

EasyCPDLC-Loaded must be the only Hoppie client using that callsign.

---

## You're done

The app is fully usable now with mouse and keyboard. Continue to the
[CDU walkthrough](2-CDU-GUIDE.md).

Everything below is optional hardware integration.

---

# Optional A · MSFS WASM module

**What it does:** lets physical buttons, encoders and LEDs drive the panel through
MobiFlight. It carries key presses *in* and lamp states *out*.

**You do not need the MSFS SDK** — a prebuilt module ships in the release.

### A1. Copy the folder

In the release, open **`1 - MSFS Community Folder`**. Copy the whole
**`easycpdlc-vns430-bridge`** folder — not just the `.wasm` — into your MSFS 2024
Community folder:

```text
Steam      %APPDATA%\Microsoft Flight Simulator 2024\Packages\Community
MS Store   %LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community
```

Can't find it? Open `UserCfg.opt` for MSFS 2024, read the `InstalledPackagesPath`
line, and use the `Community` folder beneath that path.

When you're done it must look exactly like this:

```text
...\Community\easycpdlc-vns430-bridge\manifest.json
...\Community\easycpdlc-vns430-bridge\layout.json
...\Community\easycpdlc-vns430-bridge\modules\easycpdlc-vns430-bridge.wasm
```

### A2. Restart MSFS

**Mandatory.** MSFS only scans the Community folder at startup.

### A3. Verify

1. Load into any flight.
2. Start **MobiFlight Connector**.
3. Start EasyCPDLC-Loaded and right-click the tray icon. It should read:

   ```text
   MobiFlight module: connected
   ```

If it says *not detected*, the folder is in the wrong place, the structure is wrong,
or MSFS wasn't restarted.

### A4. Enable hardware keys

On the CDU: `SETUP` → **HW KEYS** → `ON`.

This gate exists so a stray variable can never press keys while you're flying by
mouse. It is `OFF` by default.

---

# Optional B · vPilot bridge

**What it does:** imports vTDLS PDC clearances and controller "Contact Me" alerts
from vPilot into your inbox. It does not use Hoppie and does not create a CPDLC
session — these are review-only.

### B1. Close vPilot

Completely. The installer will refuse otherwise, because the plugin file is locked
while vPilot runs.

### B2. Run the installer

In the release, open **`2 - vPilot Bridge`** and double-click:

```text
Install-vPilot-Bridge.cmd
```

It copies `EasyCPDLC.VPilotBridge.dll` into your vPilot `Plugins` folder and prints
the SHA-256 of what it installed.

### B3. Verify

Start vPilot and type:

```text
.debug
```

`EasyCPDLC vPilot Bridge` should appear in the loaded plugin list.

---

# Optional C · MobiFlight profiles

**What it does:** maps your physical board's buttons to CDU keys, and your LEDs to
the CDU annunciators.

In the release, open **`3 - MobiFlight Profiles`**:

| Profile | Use for |
|---|---|
| `EasyCPDLC-WinWing-737-CDU.mfproj` | **Start here for a WinWing CDU.** 71 key inputs + 5 lamp outputs |
| `EasyCPDLC-DCDU-Module.mfproj` | A simpler board: just the 12 line-select keys + a few actions |
| `EasyCPDLC-VNS430-Module.mfproj` | The GNS430 instrument instead of the CDU |

### C1. Import

MobiFlight Connector → **File → Open** → pick the `.mfproj`.

### C2. Reassign to your hardware

Every row ships bound to a **placeholder controller**, so all rows show as unassigned
on import. **This is expected, not an error.**

For each row on the **Input** tab, change the device to your board and pick the pin or
button. On the **Output** tab, do the same for the five lamps.

**Leave the command / source column alone** — that's the part that's already correct.

### C3. Test

Tray → **CDU tools → CDU annunciator lamp test** lights all four annunciators at once
so you can confirm your LED wiring without flying.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Messages go missing, or only some arrive | The aircraft's own Hoppie is still enabled. Set it to NONE. |
| `<SETUP` is amber | A required credential is missing — `SETUP` → `<ACCOUNT`. |
| Tray: *module: not detected* | Wrong Community folder, wrong structure, or MSFS not restarted. |
| Hardware keys do nothing | `SETUP` → **HW KEYS** is `OFF`, or profile rows still on the placeholder controller. |
| vPilot installer fails | vPilot is still running. Close it completely. |
| SmartScreen blocks the app | *More info → Run anyway.* The app is unsigned. |

---

**Next:** [2 · CDU walkthrough](2-CDU-GUIDE.md) · [3 · GNS430 walkthrough](3-GNS430-GUIDE.md)
