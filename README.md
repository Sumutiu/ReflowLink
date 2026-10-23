# ReflowLink

Control software for reflow and BGA rework stations that use the **ALTEC PC410** temperature controller.

© 2026 Marius Sumutiu. All rights reserved.

## Supported stations

| Station | What the PC410 controls | Controller units | Limit |
|---|---|---|---|
| **ACHI IR6500** | 400 W top IR heater (the bottom preheater has its own controller) | °C/s, seconds (confirmed) | 260 °C |
| **ACHI IR6000** | Upper IR heater (the bottom heater uses a CH6 controller) | °C/s, seconds (ACHI manual) | 260 °C |
| **ACHI IR-PRO-SC** | Upper heater (the bottom preheat module has its own controller) | °C/s, seconds (as other ACHI models; confirm with a test run) | 260 °C |
| **Other PC410 / AL808 station** | Whatever heater the controller drives | You choose: ramp per second or per minute, hold in seconds or minutes | 260 °C (editable) |

You pick the station the first time ReflowLink starts, and can change it under **Settings → Station**. The choice sets the controller's units and the temperature limit. Profiles are always edited in °C per second and seconds; ReflowLink converts them to the controller's units when it reads or writes a pattern.

## Temperature limits

There are two limits, and a profile has to respect both:

- **ReflowLink's limit** (*Settings → Profiles → Maximum temperature*). It defaults to the controller's factory setpoint limit (SPH = 260 °C on all listed stations). You can raise it up to 400 °C; ReflowLink asks you to confirm, because it doesn't change the controller.
- **The controller's setpoint limit** (`HS`, plus `LS` as the lower limit), set in the PC410 by the station maker (260 °C from the factory) and adjustable on the front panel as SPH / SPL. ReflowLink reads it when connecting and shows it in Settings.

When you write a profile, ReflowLink first checks every target against `HS`/`LS` and refuses the write if one is outside, without changing the pattern. The PC410 answers each written value with ACK (accepted) or NAK (rejected), and ReflowLink also reads the targets back in case a controller limits a value instead of rejecting it. If the controller rejects or changes a target during the write, ReflowLink stops, clears the pattern (ends it at step 1) so it can't run with partial settings, lists the rejected values, and won't start that pattern until a valid profile is written to it. Before every start, the pattern is read back and checked against both limits.

## Control Tuning

The *Control Tuning* page shows the controller's PID and output settings, explains what each one does, and lets you change them. ReflowLink reads them every time it connects; while offline it shows the factory values of an ACHI IR6500 (P 10, I 61, D 9, output limit 100 %, cycle time 3.0 s, sensor break power 0 %).

- **PID graph:** a simulated heat-up on a generic IR heater model, comparing the controller's current P, I, D and output limit with your edits. Hovering a setting highlights the part of the curve it affects. It shows the direction and rough size of a change, not an exact prediction for your station.
- **Writing:** only changed settings are written, after a confirmation; each one is read back. Writing is locked while a program or auto-tune runs, and until the controller has been read on the current connection.
- **Auto-tune** (`XS`): starts the controller's own tune, with warnings. ReflowLink shows when it's running, blocks program starts meanwhile, and shows the new P, I and D when it ends.
- **Advanced:** cutback (`HB`/`LB`), alarms (`HA`/`LA`/`DA`), program repeat count (`Lc`) and ramp-to-setpoint rate (`SR`). ACHI hides these on the front panel; they're editable with an extra warning.

Parameter codes: `XP` (P), `TI` (I), `TD` (D), `HO` (output limit), `CH` (heat cycle time), `BP` (sensor break power). Note that `HB` (high cutback) and `Hb` (holdback) are different parameters.

## What it does

- **Dashboard**: live temperature (PV), setpoint (SP) and heater output, plotted on a chart with the planned profile drawn over it. Start, hold, resume and stop the program on the controller. Every run is recorded to CSV automatically.
- **Profiles**: an 8-step editor (ramp °C/s → target °C → hold s) with a live preview, total time and peak temperature. Profiles are saved as `.rlprofile` files (profiles saved by IR6500 Studio as `.ir6profile` still open), and you can read or write any of the controller's 10 patterns (0–9) with read-back verification.
- **Console**: the raw serial traffic (TX/RX, decoded and in hex), plus manual read or write of any parameter for troubleshooting.
- **Settings**: station, COM port, baud rate, address, timeouts, DTR/RTS, temperature limit, logging folder, and a **built-in simulator** so you can try everything without a station.

## Requirements

- Windows 10 or 11 (.NET Framework 4.8 is already installed on both).
- A serial connection to the PC410. This is usually a USB-to-RS232 adapter on the station's communication port.

## Running

`ReflowLink.exe` is a single file with no installer and no extra DLLs. Copy it anywhere and run it.
The `ReflowLink.exe.config` that the build produces is optional.

Windows may show a SmartScreen prompt the first time, because the exe is not code-signed (*More info → Run anyway*).

**Upgrading from IR6500 Studio:** on first start ReflowLink imports your settings, selects the ACHI IR6500 and moves `Documents\IR6500 Studio` (profiles and logs) to `Documents\ReflowLink`.

## Building with Visual Studio 2026

1. Open `ReflowLink.slnx`.
2. Select **Release** and build (*Build → Build Solution*).
3. The exe is written to `src\ReflowLink\bin\Release\ReflowLink.exe`.

The project is SDK-style and targets `net48`. If the *.NET Framework 4.8 targeting pack* component is not installed, the build restores the reference assemblies from NuGet automatically.

`tests\ReflowLink.Tests` is a small console test runner with no dependencies (set it as the startup project and press F5). It checks the protocol framing and checksums against the documented examples, unit conversion for seconds- and minutes-based controllers, and does full profile uploads/downloads and program runs against the simulator.

### Project layout

```
src/ReflowLink/
  Protocol/   Pc410Protocol (frames, BCC), Pc410Client (transactions, retries),
              Pc410Device (telemetry, profiles, run/hold/stop), SerialTransport, SimulatedPc410
  Model/      Stations (supported stations, controller units), ReflowProfile (+ file format,
              presets, planned curve), AppSettings, CsvRecorder
  Services/   DeviceService (connection + background polling)
  UI/         Theme, custom dark controls, chart, pages, station picker, main window
tests/ReflowLink.Tests/
```

### Adding a station

Add an entry to `StationCatalog.All` in `src/ReflowLink/Model/Stations.cs`: an id, the name, what the PC410 controls, notes, the temperature limit and the controller units (`ControllerUnits.SecondsBased` or `MinutesBased`). It then appears in the first-run picker and in Settings.

### Code style

`.editorconfig` holds the formatting rules (1TBS braces, 4-space indent, CRLF) and the modern C# syntax Visual Studio should suggest. Visual Studio applies them when you format a document (Ctrl+K, Ctrl+D) or on Code Cleanup, and `dotnet format` uses them too.

## Protocol notes (PC410 / AL808)

- **Serial format:** 7 data bits, even parity, 1 stop bit (**7E1**), 300–19200 baud.
- **Read:** `EOT A A B B C1 C2 ENQ` → `STX C1 C2 DATA ETX BCC`. Each address digit is sent twice, so address 01 is sent as `0011`.
- **Write:** `EOT A A B B STX C1 C2 DATA ETX BCC` → `ACK` or `NAK`.
- **BCC** is the XOR of every byte after STX, including ETX.
- **Parameters used:** `PV` `SP` `OP` (telemetry), `OS` (program: `>0000` stop, `>0002` run, `>0003` hold), `SW` (status), `SE` (segment), `ch` (pattern 0–9), `Hb` (holdback), and `r1–r8` / `l1–l8` / `t1–t8` (ramp, target °C, hold, in the controller's units).
- **Unused steps:** by default they are written as `0/0/0`. You can choose to write `END` on the first unused ramp instead (in Settings).

## Troubleshooting

| Symptom | Check |
|---|---|
| "The controller did not answer" | COM port, baud rate (must match the controller), address, cable. The format must be 7E1. |
| Device Manager: "PL2303TA does not support Windows 11 or later" | Install an older Prolific driver (3.9.0.2 or 3.8.x) via *Update driver → Browse → Let me pick*, and stop Windows Update replacing it. An FTDI-based USB-RS232 adapter avoids the problem. |
| "Port is in use" | Close PSoft or any terminal program that has the COM port open. |
| Works with PSoft but not here | Compare its baud rate and address. Try toggling DTR/RTS (some isolated adapters take power from them). |
| A profile runs 60× too fast or too slow | The station's units don't match the controller. Choose the right station, or for *Other PC410 / AL808 station* switch the ramp and hold units. |
| Values read back slightly different after writing | The controller rounds some values. The warning dialog lists each difference. |
| "Profile rejected by the controller" or a target "above the controller's setpoint limit" | The target is above the controller's `HS`. Lower it, or raise `HS` in the controller's configuration (and ReflowLink's limit in Settings). |

## Files and folders

- Settings: `%APPDATA%\ReflowLink\settings.xml`
- Profiles (default): `Documents\ReflowLink\Profiles`
- CSV logs (default): `Documents\ReflowLink\Logs`

## Safety

The station heats to soldering temperatures. The controller keeps running a program even if this software closes or loses the connection. Never leave a run unattended. Verify profiles with an external thermocouple before using them on valuable boards.
