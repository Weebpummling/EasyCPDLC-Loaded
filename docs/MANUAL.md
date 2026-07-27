# EasyCPDLC-Loaded — Manual

A hardware and software bridge for simulated datalink systems.

This manual covers everything from unzipping to flying. If you only read one thing,
read [Quick start](#quick-start).

---

## Contents

1. [What is in this package](#what-is-in-this-package)
2. [Quick start](#quick-start)
3. [⚠️ Before you connect](#-before-you-connect)
4. [First-run setup](#first-run-setup)
5. [Using the CDU](#using-the-cdu)
6. [Workflows](#workflows)
7. [Optional: MSFS bridge for hardware keys](#optional-msfs-bridge-for-hardware-keys)
8. [Optional: mirror to a WinWing CDU](#optional-mirror-to-a-winwing-cdu)
9. [Optional: vPilot bridge](#optional-vpilot-bridge)
10. [Optional: printing](#optional-printing)
11. [Company position reports](#company-position-reports-fmc-wpr)
12. [Test mode](#test-mode)
13. [Troubleshooting](#troubleshooting)

---

## What is in this package

```text
EasyCPDLC.exe                  <- the app. Run this.
MANUAL.md                      <- this file
START-HERE.txt

1 - MSFS Community Folder\     <- OPTIONAL: hardware keys in MSFS
2 - vPilot Bridge\             <- OPTIONAL: vTDLS PDCs + Contact Me alerts
3 - MobiFlight Profiles\       <- OPTIONAL: key and lamp bindings

Docs\                          <- full documentation
release-manifest.json
```

Everything numbered is **optional**. The app runs on its own with mouse and keyboard.

---

## Quick start

1. Extract the whole ZIP somewhere permanent (not inside the ZIP viewer).
2. Run **`EasyCPDLC.exe`**.
3. Right-click the tray icon → **Connection credentials…** and enter your **VATSIM
   CID** and **Hoppie logon code**. (SimBrief / eLoadControl / SayIntentions are
   optional — add them if you use those features.)
4. On the CDU: `MENU` → `<DLK` → `<CONNECT`, then **`EXEC`**.
5. Read [Before you connect](#-before-you-connect) first — it matters.

That is the whole software install. No dependencies, no runtime to install.

---

## ⚠️ Before you connect

**Turn off your aircraft's own Hoppie/ACARS.**

If your aircraft has built-in Hoppie support (PMDG, Fenix, iniBuilds, FSLabs,
ToLiss…), set its network to **NONE** and clear its logon code.

Hoppie delivers each message **once**, to whichever client asks first. Two clients on
the same callsign means your messages get split unpredictably between them — some land
in the aircraft, some here, and neither shows the whole conversation.

EasyCPDLC-Loaded must be the only Hoppie client using that callsign.

---

## First-run setup

Credentials are stored encrypted for your Windows account (DPAPI). They are never
written to logs.

Enter them either way:

- Tray → **Connection credentials…**, or
- CDU → `SETUP` → `<ACCOUNT` (type into the scratchpad, then press the line key)

| Credential | Needed for |
|---|---|
| **VATSIM CID** | VATSIM identity |
| **Hoppie logon code** | **Required** — all ACARS/CPDLC traffic |
| **SimBrief** username/ID | Flight plan + loadsheet source data |
| **eLoadControl API key** | Loadsheet generation |
| **SayIntentions API key** | SayIntentions network + weather |

If something required is missing, the `<SETUP` item on the CDU menu turns **amber**.

### Pick your network and weather

There is no global network or weather switch. Both are chosen where they apply:

- **LOGON VIA**, on the `LOGON` page — which network CPDLC logons and `REQ CLR` use
- **VIA**, on each AOC request page (`TELEX`, `METAR`, `ATIS`, `OCEANIC`, `POS RPT`)
  — which network that one request goes out on

On METAR and ATIS the choices are `VATSIM` / `REAL WORLD` / `SI`. `REAL WORLD` pulls
genuine METAR/TAF and real D-ATIS; both it and `SI` arrive without a datalink
connection. The selector opens on whatever you picked last.

### What SI mode changes

On `SI`, the **datalink itself** moves to the SayIntentions ACARS network — not just
the weather:

- **PDC and CPDLC** go to SayIntentions' always-on ATSU, station **`PKGM`**, using
  your **SI API key**. No VATSIM connection is needed; `REQ CLR` and `LOGON` work as
  soon as the key and a **filed SimBrief plan** are in place. SI's ATC identifies
  your flight by that SimBrief plan, so file the OFP before requesting.
- **Your VA's ACARS keeps working.** The app polls Hoppie *and* SayIntentions at the
  same time: ATC traffic arrives from SI, VA telex and loadsheets from Hoppie.
- **Do not run another SI ACARS client at the same time** — SayIntentions' own
  "ACARS Bridge" app, or an aircraft connected to SI ACARS directly, competes for
  the same messages exactly like a second Hoppie client would.
- **LOGON VIA** (on the CDU's `LOGON` page, or the GNS430 menu) picks which network
  CPDLC logons and the clearance request use: `AUTO` follows the ATC network, `SI` or
  `VATSIM` forces one side. SayIntentions hands flights off to VATSIM controllers on
  their end, so a pilot can be on both at once and take the PDC from either.
- **ATC NETWORK** itself is only settable from the **GNS430 menu**. On the CDU it is
  inferred: `LOGON VIA` decides per logon, and each AOC request decides per request.

### Pick your instrument

Tray → **Instrument** → **737 CDU** or **GNS430**, or CDU `SETUP` → **INSTRUMENT**.
Only one runs at a time; selecting one hides the other.

---

## Using the CDU

The 737 CDU is driven by the twelve line-select keys (`L1`–`L6`, `R1`–`R6`), the
keypad, and `CLR` / `EXEC` / `MENU` / `PREV PAGE` / `NEXT PAGE`. Everything is
clickable, and the keyboard works too (`F1`–`F6` = left keys, `F7`–`F12` = right).

### The EXEC rule

Anything that **transmits** or **destroys data** is armed first — it highlights and the
`EXEC` light comes on. Press **`EXEC`** to actually do it. Navigation is immediate.

This is your safety net: nothing is sent by a stray keypress.

### Annunciators

- **MSG** — an inbound message is unread
- **FAIL** — the CDU is showing an error (press `CLR` to clear it)

### Menu map

```text
MENU
 ├─ <DLK      VATSIM session, ATC logon, live status, reload flight plan
 ├─ <ATC      CPDLC requests (direct, level, speed, when can we, free text) + POS REP
 ├─ <AOC      telex, METAR, ATIS, loadsheet, oceanic + company POS RPT and address
 ├─ <MSG      inbox: RECEIVED / SENT / clear all
 └─ <SETUP    account, printer, technical, winwing, instrument, hardware keys
```

---

## Workflows

### Connect and log on to ATC

1. `MENU` → `<DLK` → `<CONNECT` (LSK2), then **`EXEC`** — it reads `<DISCONNECT` in
   green once the session is up
2. `<LOGON` — pick an online CPDLC facility from the list, or type a 4-letter code into
   the scratchpad and press `MANUAL LOGON` at LSK5
3. Press **`EXEC`** to send the logon

There is nothing to connect on SI: with the API key and a filed SimBrief plan the
datalink is live immediately. `<CONNECT` is the VATSIM session specifically.

### Send a request

1. `MENU` → `<ATC` → pick the request type
2. Fill the fields using the scratchpad
3. `SEND>` then **`EXEC`**

### Read and answer a clearance

1. `MSG` lights → `MENU` → `<MSG` → `<RECEIVED`
2. Select the message
3. `<WILCO` / `<UNABLE` / `<STANDBY`, then **`EXEC`**

Long messages scroll with `PREV PAGE` / `NEXT PAGE`.

### Get weather

`MENU` → `<AOC` → `<METAR` or `<ATIS`, enter the ICAO, pick the source on the **VIA**
line, send. Real-world and SayIntentions sources arrive without needing a datalink
connection.

### Generate a loadsheet (eLoadControl)

1. Set your **SimBrief** user and **eLoadControl API key** first
2. `MENU` → `<AOC` → `<LOADSHEET`
3. Pick aircraft variant, cabin, and format; confirm the passenger split
4. `GENERATE>` then **`EXEC`** (this uses one eLoadControl API request)
5. The loadsheet lands in `<RECEIVED`, tagged `LOADSHEET`, and can be printed

### Virtual-airline ACARS

If your VA sends TELEX messages or loadsheets over Hoppie, they arrive in the same
inbox automatically. Anything that looks like a loadsheet is tagged `LOADSHEET`
whether you generated it or your VA sent it. Nothing to configure.

---

## Optional: MSFS bridge for hardware keys

Lets physical buttons, encoders and LEDs drive the panel through MobiFlight.

1. Open **`1 - MSFS Community Folder`**
2. Copy the whole **`easycpdlc-vns430-bridge`** folder into your MSFS 2024
   **Community** folder:
   - Steam: `%APPDATA%\Microsoft Flight Simulator 2024\Packages\Community`
   - MS Store: `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community`
3. **Restart MSFS** (it only scans Community at startup)
4. Start MobiFlight Connector
5. In the app: CDU `SETUP` → **HW KEYS** → `ON`
6. Import a profile from **`3 - MobiFlight Profiles`** and reassign each row to your
   own board

The tray shows **MobiFlight module: connected / not detected** so you can confirm it.

A prebuilt module is included — you do **not** need the MSFS SDK.

---

## Optional: mirror to a WinWing CDU

Puts this screen on a real WinWing CDU/MCDU/PFP.

1. In **SimAppPro**, set the unit to `OBSERVER` (recommended — the 737 only has
   captain and first-officer CDUs, so nothing in the sim ever uses the observer unit
   and it cannot clash with your aircraft's own CDU)
2. **Close SimAppPro completely** — it and MobiFlight cannot both hold the CDU
3. Start **MobiFlight Connector**
4. In the app: CDU `SETUP` → `<WINWING` → pick the matching seat, then press **`EXEC`**

`LINK` shows `WAITING` until it connects, then `SENDING`.

The seat **resets to OFF every launch on purpose**, so the app can never take over a
CDU that is already showing a live aircraft display.

Lamp outputs (`MSG`, `CALL`, `FAIL`, `OFST`, `EXEC`) ship pre-wired in the WinWing
profile — just reassign the device on MobiFlight's Output tab.

---

## Optional: vPilot bridge

Imports vTDLS PDC clearances and controller "Contact Me" alerts from vPilot.

1. **Close vPilot**
2. Open **`2 - vPilot Bridge`** and run **`Install-vPilot-Bridge.cmd`**
3. Restart vPilot; type `.debug` to confirm `EasyCPDLC vPilot Bridge` is loaded

These are review-only: they do not use Hoppie, do not create a CPDLC session, and do
not enable `REQ CLR`.

---

## Optional: printing

A **thermal receipt printer** gives the best experience — no ink, fast, quiet, and it
cuts the strip for you. Any ESC/POS receipt printer Windows can install will work.

Set it up in CDU `SETUP` → `<PRINTER`: pick the queue, mode (`ESC/POS` recommended),
paper profile (`4 INCH` or `80MM`), cut and feed lines. Run a **mock file** test first,
then a real test print.

> **Buying a 3D-printed facade?** Measure your actual printer against the model's
> stated dimensions first — not just the "80 mm" paper width. Receipt printers vary a
> lot in body size and where the cut slot sits, and a facade cut for another chassis
> will not line up.

---

## Company position reports (FMC WPR)

A position report to your operator, prefilled from the route and your live position —
the AOC service ICAO calls **E1**, *FMC waypoint position reporting over ACARS*.

`MENU` → `<AOC`. The right column shows **COMPANY** — your operator's Hoppie address,
or **NOT SET** in amber. Type an address into the scratchpad and press the line key to
set it; press it with an empty scratchpad to clear it. It is an ordinary Hoppie
recipient, not a credential, which is why it lives here rather than in the tray.

With an address set, `POS RPT>` opens the report already filled in:

| Field | Prefilled from |
|---|---|
| `COMPANY` | the address you set |
| `OVERFLEW` | the last enroute fix the app saw you pass |
| `TIME Z` | now |
| `FL` | your altitude |
| `NEXT FIX` / `NEXT ETA` | the next fix, and an ETA from your ground speed |
| `THEN FIX` | the one after |
| `VIA` | `HOPPIE` — company traffic, whichever network ATC is on |

Correct anything the sim got wrong, then `SEND>` and **`EXEC`** like any other request.
It sends:

```text
POS01 DLH6YM /OVR ETRAT 1423 F350 /PSN N4712.3 W00812.4 /NXT ROLIS 1442 /FLW OSMEL /GS 468
```

**Nothing is sent automatically.** The app tracks where you are along the route so the
page opens filled in; the transmit is always yours. Report numbering (`POS01`, `POS02`…)
advances only on a report that actually goes out, and resets when you load a new plan.

Position comes from the sim — via the MSFS module if it's running, otherwise FSUIPC.
Without either, the coordinates are simply left out rather than reported as `0N 0E`.

SID and STAR fixes are skipped deliberately: they sequence every couple of minutes, so
tracking them would leave the page pointing at a departure fix for the whole cruise.

> This is separate from the `POS REP` on the ATC pages. That one you compose yourself
> and send to a controller; this one is prefilled and goes to your airline.

---

## Test mode

Tray → **CDU tools** → **Test mode (no network traffic)**.

Fakes a connected session with sample messages already in the inbox, so you can set up
hardware, printing and the WinWing mirror with no VATSIM, no Hoppie and no SI. **Nothing
is transmitted while it is on** — every outbound packet is suppressed.

The tick in the menu is the switch position. Turning it off clears the faked session and
leaves you cleanly disconnected; it cannot be turned on during a live session, so
disconnect first. It is never remembered across launches.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Messages go missing / only some arrive | The aircraft's own Hoppie is still on. Set it to NONE. |
| `<SETUP` is amber | A required credential is missing — check `SETUP` → `<ACCOUNT`. |
| `FAIL` light is on | The CDU is showing an error message. Press `CLR`. |
| Can't connect | Check the Hoppie logon code, and that your callsign matches VATSIM. |
| Tray says *module: not detected* | Package not in Community, wrong folder layout, or MSFS not restarted. |
| Hardware keys do nothing | `SETUP` → **HW KEYS** is `OFF`, or profile rows still on the placeholder controller. |
| WinWing `LINK` stuck on `WAITING` | MobiFlight is not running, SimAppPro is still open, or no CDU is set to that seat. |
| Printer does nothing | Wrong queue selected. Try **mock file** mode and check the preview. |

Full documentation is in the **`Docs`** folder.

---

Licensed under the GNU General Public License v3.0 or later. Not affiliated with
VATSIM, SayIntentions, Hoppie, eLoadControl, SimBrief, WinWing, MobiFlight or any
aircraft developer. Provided as-is, without warranty.
