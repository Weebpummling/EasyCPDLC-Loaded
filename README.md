# EasyCPDLC-Loaded

![Version](https://img.shields.io/badge/version-1.0.0--beta-yellow)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![Networks](https://img.shields.io/badge/networks-VATSIM%20%C2%B7%20SayIntentions-blue)
![Datalink](https://img.shields.io/badge/datalink-Hoppie%20ACARS-blue)

**EasyCPDLC-Loaded is a hardware and software bridge for simulated datalink
systems.**

The goal is to get ACARS/CPDLC off your main monitor and onto something you can
actually touch. It drives:

- **WinWing CDU hardware** — the screen is mirrored to a real WinWing CDU/MCDU/PFP
  through MobiFlight, and its keys drive the app back. Captain, First Officer, and
  Observer units are all supported.
- **Any small screen** — a spare monitor, a USB display panel, a tablet. Run the
  instrument bare (no bezel artwork) and it becomes a clean cockpit display.
- **A touchscreen** — every key and line-select is clickable, so a cheap touch panel
  turns into a working CDU with no other hardware at all.
- **A thermal printer** — print clearances, ATIS, and loadsheets as real paper strips.

On the software side it presents one datalink backend through swappable cockpit
**instruments**. Two ship in 1.0.0-beta:

- a **Boeing 737 CDU** (an LSK + keypad MCDU front end), and
- a **GNS430** desktop unit (a knob-and-key front end).

Both share one CPDLC/ACARS backend, one set of saved credentials, and one
weather/ATC-network configuration. You pick which instrument is on screen; only one
runs at a time. The legacy Airbus/Boeing 2D DCDU skins are hidden in this release
while a replica Airbus DCDU is rebuilt to rejoin the instrument selector later.

## Why it exists

It was built so a pilot with CDU hardware can use **that hardware** to work the ACARS
system, and switch networks without relearning anything. The same physical unit talks
to **VATSIM** or **SayIntentions** — you change one setting, not your workflow.

It also folds in **eLoadControl's** loadsheet generator, so a proper weight-and-balance
loadsheet is a few line-selects away and prints on the same strip as everything else,
instead of living in a browser tab on another screen.

And because it is a real Hoppie client, your **virtual airline's ACARS still works**:
VA TELEX messages and VA-issued loadsheets arrive in the same inbox, and loadsheets are
tagged automatically however they reached you. See
[Virtual-airline ACARS over Hoppie](#virtual-airline-acars-over-hoppie).

> **Flight simulation only.** Not approved for real-world aviation, dispatch,
> communications, loading, or any safety-critical use.

> ### ⚠️ Do not run the aircraft's own Hoppie connection
>
> If your aircraft has a built-in Hoppie/ACARS setup (PMDG, Fenix, iniBuilds, FSLabs,
> ToLiss…), set its network to **NONE** and clear its logon code before connecting.
>
> Hoppie delivers each message **once**, to whoever asks first. Two clients on the same
> callsign means messages are split unpredictably between them — some land in the
> aircraft, some here, and neither shows the full conversation. EasyCPDLC-Loaded must be
> the only Hoppie client using that callsign.

---

## Contents

1. [Requirements](#requirements)
2. [Installation](#installation)
3. [Setup](#setup)
4. [Boeing CDU usage guide](#boeing-cdu-usage-guide)
5. [GNS430 usage guide](#gns430-usage-guide)
6. [Virtual-airline ACARS over Hoppie](#virtual-airline-acars-over-hoppie)
7. [Printing](#printing)
8. [Hardware setups](#hardware-setups)
9. [Security and privacy](#security-and-privacy)
10. [Build from source](#build-from-source)
11. [Credits, license, disclaimer](#credits-license-disclaimer)

---

## Requirements

- Windows 11 x64
- A **VATSIM CID** and **Hoppie ACARS logon code** for network CPDLC
- Optional **SimBrief** account / pilot ID (flight plan + loadsheet data)
- Optional **eLoadControl** account and API key (loadsheet generation)
- Optional **SayIntentions** API key (SayIntentions network + weather)
- Optional **thermal receipt printer** installed in Windows (paper strips)
- Optional **WinWing CDU/MCDU/PFP** + MobiFlight (mirror the screen to real hardware)
- Optional MSFS + MobiFlight for physical keys, encoders and annunciator lamps

The app publishes as a self-contained Windows x64 executable. No SDK is required to
run a release build.

---

## Installation

1. Download the latest **EasyCPDLC-Loaded 1.0.0-beta** release build (or build from source —
   see [Build from source](#build-from-source)).
2. Extract the folder anywhere you like and run `EasyCPDLC.exe`.
3. The app starts directly into the last-used instrument (default: the **737 CDU**)
   and adds an **EasyCPDLC** icon to the Windows system tray. There is **no** startup
   login prompt — credentials are entered once and remembered (see Setup).
4. Optional: install the MobiFlight/WASM bridge if you plan to drive the GNS430 with
   physical hardware — see [Hardware control](#hardware-control-mobiflight--wasm).

The system-tray menu is the app's control center:

| Tray item | What it does |
|---|---|
| **Show / Hide EasyCPDLC** | Show or hide the active instrument window |
| **Connection credentials…** | Open the shared credential editor |
| **Instrument ▸** | Switch between **737 CDU** and **GNS430** |
| **MobiFlight module:** *status* | Read-only: shows whether the WASM module is connected (i.e. whether hardware keybinds will work) |
| **Display ▸** | Panel artwork, on-screen buttons, display settings, reset window size |
| **CDU tools ▸** | Annunciator lamp test, test vPilot Contact Me |
| **Exit EasyCPDLC** | Quit |

---

## Setup

### 1. Enter your credentials (once)

Credentials are managed centrally and stored **DPAPI-protected for your Windows
account** — never written to the repo or to logs. Enter them either way:

- **Tray → Connection credentials…**, or
- **CDU → `SETUP` → `<ACCOUNT`** (type a value into the scratchpad, press the LSK).

| Credential | Needed for |
|---|---|
| **VATSIM CID** | VATSIM identity |
| **Hoppie logon code** | *Required* for all Hoppie ACARS / CPDLC traffic |
| **SimBrief username / pilot ID** | Flight plan, route, and loadsheet source data |
| **eLoadControl API key** | Direct loadsheet generation (bring-your-own-key) |
| **SayIntentions API key** | SayIntentions network + SayIntentions weather |

If a required credential is missing, the **`SETUP`** item on the CDU menu (and the
`<ACCOUNT` line) is highlighted amber — Hoppie and SimBrief are always flagged, plus
the credential the **active ATC network** needs to connect.

To see the full stored values at any time, open **`SETUP` → `<TECHNICAL`** (a
full-screen, paged dump of every code and setting). The ACCOUNT page itself only shows
`SET` for secret keys.

### 2. Choose your ATC network and weather source

On the **CDU `SETUP`** page (right column), or the **GNS430 `MENU`**:

- **ATC NETWORK** — `VATSIM` or `SI` (SayIntentions). *IVAO is planned.*
- **WX SOURCE** — `AUTO` (follow the network), or force `VATSIM` / `REAL WORLD` /
  `SAYINTENTIONS`.
- **HW KEYS** — `ON` lets a physical CDU drive the panel (see
  [hardware setups](#hardware-setups)).

Left column: `<ACCOUNT`, `<PRINTER`, `<TECHNICAL` (all stored values), and
`<WINWING` (mirror the screen to a WinWing unit).

![CDU SETUP page](assets/screenshots/cdu-setup.png)

Weather routing:

| Source | METAR / TAF | ATIS | Needs a network connection? |
|---|---|---|---|
| **VATSIM** | Hoppie `INFOREQ` datalink | Hoppie / VATSIM D-ATIS | Yes (connected to VATSIM) |
| **REAL WORLD** | aviationweather.gov | Real D-ATIS (datis.clowd.io) | No — fetched directly over HTTP |
| **SAYINTENTIONS** | SayIntentions `getWX` | SayIntentions `atis_cpdlc` + active runways | No — needs your SI API key |

`AUTO` uses VATSIM weather on the VATSIM network and SayIntentions weather on the SI
network. REAL WORLD and SAYINTENTIONS results drop straight into your inbox without a
datalink connection.

### 3. Choose your instrument

**Tray → Instrument → `737 CDU`** or **`GNS430`**, or cycle **CDU `SETUP` → `INSTRUMENT`**.
Instruments are mutually exclusive: selecting the GNS430 hides the CDU window and vice
versa.

---

## Boeing CDU usage guide

The 737 CDU is a 24×14 character MCDU driven by the twelve line-select keys (`L1–L6`,
`R1–R6`), an alphanumeric keypad, and the function keys `CLR`, `EXEC`, `MENU`, and
`PREV/NEXT PAGE`. Everything is clickable on screen and drivable from hardware L-vars.

**The EXEC-arm rule.** Any action that transmits on the network or destroys data is
**armed first** (it highlights and the EXEC light comes on); pressing **`EXEC`**
carries it out. Local navigation is immediate. The side annunciators show **`MSG`**
(unread inbound) and **`FAIL`** (an error is displayed).

**Main menu** (`MENU`): `<DLK` (status/connection), `<ATC` (CPDLC requests),
`<AOC` (telex + weather), `<MSG` (inbox), `<SETUP`.

![CDU main menu](assets/screenshots/cdu-menu.png)

`<MSG` turns amber and shows the unread count when inbound traffic is waiting;
`<SETUP` does the same when a required credential is missing.

### CDU workflow — VATSIM

1. **Credentials & network.** `SETUP → ACCOUNT`: CID + Hoppie code. `SETUP → ATC
   NETWORK = VATSIM`.
2. **Connect.** `MENU → <DLK → <CONNECT`. The right column shows `VATSIM CONNECTED`.
3. **Log on to ATC.** `<DLK → <LOGON`. Pick an online CPDLC facility from the
   candidate list (or type a 4-letter code and select `<LOGON`), then press **`EXEC`**
   to send the logon.
4. **Send a request.** `MENU → <ATC`, choose e.g. `DIRECT`, `LEVEL`, `SPEED`,
   `WHEN CAN WE`, `FREE TEXT`, or `POS REP>`. Fill the fields via the scratchpad,
   then `SEND>` and press **`EXEC`**.

   | ATC request menu | A request form |
   |---|---|
   | ![ATC requests](assets/screenshots/cdu-atc-requests.png) | ![Request form](assets/screenshots/cdu-request-form.png) |
5. **Read replies.** Inbound clearances light `MSG`. Open `MENU → <MSG → <RECEIVED`,
   select the message, and reply with `<WILCO` / `<UNABLE` / `<STANDBY` (armed →
   `EXEC`). Long messages scroll with `PREV/NEXT PAGE`.

   ![Messages menu](assets/screenshots/cdu-messages.png)

6. **Get weather.** `MENU → <AOC → METAR>` or `ATIS>`, enter the station; with the
   VATSIM source this is requested over the Hoppie datalink and returns to the inbox.

   ![AOC menu](assets/screenshots/cdu-aoc-menu.png)

   The same page carries `PDC`, `OCEANIC`, and `LOADSHEET>`.

### CDU workflow — SayIntentions

1. `SETUP → ACCOUNT`: enter your **SayIntentions API key**.
2. `SETUP → ATC NETWORK = SI` (this also makes `WX SOURCE = AUTO` resolve to
   SayIntentions), **or** leave the network as-is and set `WX SOURCE = SAYINTENTIONS`
   to only pull SI weather.
3. `MENU → <AOC → METAR>` / `ATIS>`, enter the ICAO. The METAR/TAF and the
   datalink-formatted ATIS (plus active runways) are fetched from SayIntentions and
   land in `<MSG → <RECEIVED` — **no Hoppie connection required** for the weather pull.

### CDU workflow — eLoadControl loadsheet

1. `SETUP → ACCOUNT`: set your **SimBrief** user and **eLoadControl API key**.
2. `MENU → <AOC → LOADSHEET>`.
3. The CDU fetches your SimBrief flight and matching eLoadControl configurations.
   Cycle **aircraft variant**, **cabin config**, and **output format**; confirm the
   passenger split.
4. `GENERATE>` and press **`EXEC`** (this consumes one eLoadControl API request).
5. The textual ACARS loadsheet arrives on `<RECEIVED`, tagged **`LOADSHEET`**, and
   scrolls with `PREV/NEXT PAGE`. Print it with `PRINT>` if you have a printer set up.

**Pre-departure clearance (PDC):** `<DLK → <LOGON` shows PDC status; when a clearance
is available, `REQ CLR>` is offered — arm it and press **`EXEC`**.

---

## GNS430 usage guide

The GNS430 is a knob-driven unit. It emulates the real bezel controls and remaps them
to the datalink:

| Control | Action |
|---|---|
| **Large right knob** | Cursor off: cycle the four **page groups**. Cursor on: move the selection |
| **Small right knob** | Cursor off: cycle **pages within a group**. Cursor on: edit the selected character/value |
| **CRSR** (knob push) | Toggle the cursor on/off |
| **ENT** | Activate the selection / send |
| **CLR** | Back / cancel |
| **MENU** | Open the page menu (config + actions) |
| **MSG** | Open the inbox — **press again to cycle `ALL → RECEIVED → SENT`** |
| **VLOC** | Open the full message log |
| **FPL** | ATC request menu |
| **PROC** | AOC / telex menu |
| **D→** | Logon page (prefilled with the best online facility) |
| **CDI** | Toggle the VATSIM connection |
| **RNG − / +** | Zoom the LCD text |

**Page groups:** `NAV` (Status, Messages) · `WPT` (Logon, **PDC**, ATC) ·
`AUX` (AOC, Load Control, Help) · `NRST` (Messages).

The **`MENU`** overlay carries the shared config and actions: connect/disconnect,
ATC request menu, AOC menu, **ATC NETWORK**, **WX SOURCE**, **CLEAR ALL MESSAGES**
(press twice to confirm), settings, MSFS-module toggle, and input help.

**Window:** the GNS430 is borderless (no title bar) — drag it by the bezel or the LCD
(a small drag past a click), resize from any edge, and it remembers its size and
position. Turning **panel artwork** off (tray → Display) switches it to a bare
letterboxed screen for use behind a physical unit.

### GNS430 workflow — VATSIM

1. `MENU → ATC NETWORK: VATSIM`. Enter CID + Hoppie code via **Connection
   credentials…** (or the CDU ACCOUNT page — credentials are shared).
2. Press **CDI** (or `MENU → CONNECT VATSIM`) to connect.
3. Press **D→** to open **Logon**, spin the small knob to type a 4-letter facility (or
   accept the prefilled online match), push **CRSR** then **ENT** to log on.
4. Press **FPL** for the ATC request menu, pick a request, edit fields with the knobs,
   and **ENT** to review then send.
5. Press **MSG** for the inbox; the `MSG` footer annunciator lights when inbound
   traffic is unread. Open a message, spin the small knob to pick a CPDLC reply, **ENT**
   to send.
6. **PDC:** in the `WPT` group spin to the **PDC** page; push **CRSR** then **ENT** on
   `REQUEST CLEARANCE` when one is available.

### GNS430 workflow — SayIntentions

1. Enter your **SayIntentions API key** (Connection credentials… or CDU ACCOUNT).
2. `MENU → ATC NETWORK: SI`, or leave the network and set `MENU → WX SOURCE:
   SAYINTENTIONS`.
3. Press **PROC** for the AOC menu, choose **METAR** or **ATIS**, enter the ICAO, and
   **ENT**. SayIntentions weather is fetched directly and appears in the inbox —
   press **MSG**, then **MSG** again to filter to `RECEIVED` if you like.

### GNS430 workflow — eLoadControl loadsheet

1. Set **SimBrief** user and **eLoadControl API key** (shared credentials).
2. In the `AUX` group spin to **Load Control** and **ENT** to prepare the session.
3. Use the knobs to pick aircraft / cabin / format, confirm the passenger split, then
   **ENT** on `GENERATE`.
4. The generated loadsheet arrives in the inbox tagged **`LOADSHEET`**; the GNS430
   jumps you straight to it.

---

## Virtual-airline ACARS over Hoppie

If your **virtual airline or dispatcher sends messages through Hoppie ACARS** — for
example an operational loadsheet, a flight-release note, or a free-text advisory —
those messages are addressed to your flight's Hoppie callsign and are therefore
**delivered to EasyCPDLC-Loaded, not to any other client.** They are received on the
normal Hoppie poll and stored in your inbox like any other ACARS message, so:

- Set the aircraft's internal Hoppie network to **NONE** so this app is the only client
  on your callsign (see the warning at the top).
- VA messages appear under **`<RECEIVED`** on either instrument.
- A message that looks like a loadsheet (contains `LOADSHEET` / `LOAD PLANNING` /
  `ELOADCONTROL`, or two or more weight tokens such as `ZFW` / `TOW` / `LDW`) is
  automatically **tagged `LOADSHEET`** in the list and is fully readable and scrollable
  — the same handling as a loadsheet you generate yourself.

You do not need to do anything special to receive VA ACARS; just be connected with the
correct callsign and Hoppie code.

---

## Printing

The 737 CDU can print the currently displayed datalink item — clearances, ATIS,
loadsheets — as a paper strip.

**Get a thermal receipt printer.** It is by far the best experience: no ink, no
cartridges, fast, quiet, and it cuts the strip for you. Any ESC/POS receipt printer
Windows can install will work.

Three modes are supported — **ESC/POS** (raw bytes via the Windows spooler, preferred
for receipt printers), **Windows** (rendered document), and **Mock file** (writes a
preview + hex dump, no paper). Two paper profiles: **`GENERIC 4 INCH`** (e.g. Citizen
CT-S4000) and **`GENERIC 80MM`** (e.g. Rongta RP326).

Configure it in **CDU `SETUP` → `<PRINTER`** (queue, mode, profile, cut, feed lines,
test print). Print with `PRINT LAST` / `<PRINT`; `REPRINT` reprints the last job.
Loadsheets and inbound ACARS are review-first and do not auto-print unless you enable
auto-print by category. Always run a **Mock file** test before sending to hardware.

> ### ⚠️ Measure before you buy a 3D-printed facade
>
> Printer shrouds and cockpit facades on Thingiverse / Printables / Etsy are usually
> modelled around **one specific printer**, and 80 mm receipt printers vary a lot in
> body size, paper-roll bay depth, and where the cut slot sits.
>
> Before buying or printing one, check the **actual dimensions of your printer**
> against the model's stated dimensions — not just the "80 mm" paper width. A facade
> cut for a different chassis will not fit, and the usual failures are the paper exit
> not lining up with the slot and the lid fouling when you reload the roll.

---

## Hardware setups

Everything below shares the same backend — mix and match.

| Setup | What you need | Notes |
|---|---|---|
| **WinWing CDU** (best experience) | WinWing MCDU/PFP + MobiFlight | Screen mirrored to the unit, its keys drive the app. CAPT / FO / OBS supported. |
| **Second monitor** | Any spare display | Turn off panel artwork for a clean bare screen; drag it over and leave it. |
| **Touchscreen** | Any touch panel | Every key and line-select is clickable — a working CDU with no other hardware. |
| **Custom buttons** | MobiFlight board | Bind the twelve LSKs and the keypad to your own switches. |
| **Paper strips** | Thermal receipt printer | Clearances, ATIS and loadsheets as real strips. |
| **Nothing at all** | — | Mouse and keyboard work fine. All of the above is optional. |

### Mirroring to a WinWing CDU

MobiFlight owns the CDU over USB and hosts a websocket server; EasyCPDLC-Loaded
publishes frames to it, so there is no fight over the device. Set the unit's seat in
**SimAppPro**, close SimAppPro, start MobiFlight, then choose the seat on the CDU under
**`SETUP` → `<WINWING`** (`OFF` / `CAPT` / `FO` / `OBS`).

The selection is **session-only and always starts `OFF`**, so the app can never claim a
CDU on startup that is already showing a live aircraft display.

![WINWING CDU page](assets/screenshots/cdu-winwing.png)

### Physical keys and lamps

Buttons, encoders and annunciator LEDs run through a private **MobiFlight → L-var →
MSFS WASM bridge**. The CDU's `MSG`, `FAIL`, `CALL`, `OFST` and `EXEC` lamps are
published as output variables, so a hardware unit lights exactly when the software one
does. The tray shows a live **MobiFlight module: connected / not detected** status.

A **prebuilt WASM module is included** — you do not need the MSFS SDK. Full install,
binding and lamp-wiring steps are in the
[hardware guide](EasyCPDLC/VNS430/MSFS2024Module/README.md).

---

## Security and privacy

- Never commit Hoppie codes, VATSIM credentials, or eLoadControl / SayIntentions API
  keys. If a key was ever exposed, revoke it and issue a new one.
- All secret credentials are stored **DPAPI-protected** for the current Windows account
  and are never written to logs.
- Weather and loadsheet requests go directly to their respective services using your
  own keys.

---

## Build from source

From a PowerShell prompt with the .NET 10 SDK:

```powershell
dotnet restore .\EasyCPDLC.sln
dotnet build .\EasyCPDLC.sln -c Release
dotnet test .\EasyCPDLC.Tests\EasyCPDLC.Tests.csproj -c Release
```

Publish the client:

```powershell
dotnet publish .\EasyCPDLC\EasyCPDLC.csproj -c Release
```

---

## Credits, license, disclaimer

EasyCPDLC-Loaded builds on the EasyCPDLC lineage:

- This project: [Weebpummling/EasyCPDLC-Loaded](https://github.com/Weebpummling/EasyCPDLC-Loaded) (formerly `EasyCPDLC-Modernized-Printer-eLC`)
- Immediate upstream: [fresH229a/EasyCPDLC-Modernized](https://github.com/fresH229a/EasyCPDLC-Modernized)
- Original project: [quassbutreally/EasyCPDLC](https://github.com/quassbutreally/EasyCPDLC) — © 2022 Joshua Seagrave

The upstream README is preserved in [README.UPSTREAM.md](README.UPSTREAM.md).

Licensed under the **GNU General Public License v3.0 or later**, consistent with
upstream.

This unofficial community project is not affiliated with or endorsed by VATSIM,
SayIntentions, Hoppie, eLoadControl, SimBrief, PMDG, Garmin, MobiFlight, aircraft
manufacturers, aviation authorities, or the original EasyCPDLC authors. It is provided
as-is, without warranty. Use it only for flight simulation and at your own risk.
