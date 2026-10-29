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

## Bed heat control

*Settings → Station → Bed heat* chooses how the bottom (bed) heater is wired:

- **Off (top IR only).**
- **Single PC410 (IN2 + OUT2):** the top PC410 reads the bed and ReflowLink switches it. See below.
- **Second PC410:** a controller of its own.

The sidebar groups the pages by heater and fades out the Bed Heat pages that don't apply: *Switching* is for a single PC410, *Control Tuning* and *Console* for a second PC410.

### IN2 readings (information only)

Each PC410 has a second input, IN2 (terminals 16 + / 17 −, read as `P2`). *Read IN2 (P2)* in a connection card shows it on the Dashboard, the chart and the CSV log, without any effect on the program, for example a bed thermocouple bridged from the REX-C100 to see the bed temperature:

| Bed heat | IN2 (Top IR) | IN2 (Bed Heat) |
|---|---|---|
| Off | optional | not available |
| Single PC410 | always read: it's the bed temperature (*Bed temp (IN2)*) | not available |
| Second PC410 | optional | optional |

With Top IR only, IN2 takes the second half of the Temperature tile; with a bed, it gets a row in the side panel. A thermocouple shared with another controller can read a few degrees off: compare it once with that controller's display.

### Single PC410 (IN2 + OUT2)

The bed keeps its own controller (for example the REX-C100), but its output reaches the bed SSR only through the top PC410's OUT2 relay, and the bed thermocouple is also wired to the PC410's second input:

- **Wiring:** bed thermocouple to IN2, terminals 16 (+) and 17 (−) (read as `P2`); OUT2, terminals 6–7, in series with the bed controller's output to the bed SSR.
- **PC410 setup** (configuration menu, LOC 808): `0P2 = AL02`, `AL02 = HIAL`.
- **Switching:** ReflowLink writes alarm 2: `A2 = 001` makes the high alarm active, so OUT2 closes and the bed may heat; `A2 = 500` clears it, so OUT2 opens. It only writes when OUT2 has to change, and reads `A2` back to check.
- **Regulation:** the bed setpoint follows the IR program. During a step's ramp it moves towards the step's *Bed Heat Target* as the IR setpoint moves towards its own target, and it holds the bed target during the step's hold. A PI regulator turns the difference into an on/off share of each cycle (*Bed Heat → Switching*: proportional band, integral time, cycle time). *Wait for Bed* holds the IR while the bed lags by more than the bed holdback; *Wait for IR* doesn't apply.
- **Bed targets:** no controller stores them, so ReflowLink keeps them per pattern when the profile is written, and restores them when the pattern is read back (only if the IR targets still match). A pattern without saved bed targets, or changed since, can't be started.
- **Safety:** the bed is switched off on Stop, at the end of a run, when no bed reading arrives (the IR program is stopped too), above the bed limit, before disconnecting, when changing the bed setting and when closing ReflowLink; a bed left switched on is switched off while no program runs. If ReflowLink crashes or the USB cable is pulled while OUT2 is on, nothing in software can switch it off: keep the bed controller behind OUT2 at the highest bed temperature you allow, or fit a thermostat.
- **CSV logs** get the bed columns plus `bed_out2`.

### Second PC410

A second PC410 can control the bottom (bed) heater. Choose *Second PC410* under *Settings → Station → Bed heat*, then set up *Bed Heat Connection* (its own serial port, address and units; ramp per second and hold in seconds by default) and *Bed Heat Profiles* (its own maximum temperature). The sidebar gets a second connection box, and there's a *Bed Heat Console* and *Bed Heat Control Tuning* page.

- **Profiles:** each step has an *IR Target* and a *Bed Heat Target*. Both controllers use the same ramp rate and hold time, and each has its own global holdback. Writing or reading pattern N does both controllers; both setpoint limits are checked first, and if the bed controller refuses part of its profile after the IR pattern was written, pattern N is blocked until it's written again.
- **Wait for Bed / Wait for IR (per step):** the two PC410s can't talk to each other, so ReflowLink holds whichever heater is ahead. *Wait for Bed* on a step keeps the IR from getting ahead of the bed in that step: the IR is held while the bed lags its setpoint by more than the bed holdback, or when the IR has already moved on to the next step. *Wait for IR* does the same the other way round. Put the toggle on the step where the heaters drift apart, for example the step where the bed has the longer ramp. ReflowLink remembers these choices per pattern, because the controllers can't store them.
- **Dashboard:** temperature, setpoint and output tiles show both heaters; Start, Hold and Stop act on both. If one controller doesn't start, the other is stopped again. A pattern whose two halves don't match (different steps, ramps or holds) can't be started.
- **Safety:** a resume is only ever sent to a controller that's on hold (Run on an idle PC410 would restart its pattern). If the bed controller stops answering during a run, the IR program is stopped and you're alerted. CSV logs get the bed columns and a "waiting" column.
- **Step numbering:** the waits use each controller's current segment (`SE`). If a controller counts ramp and dwell as separate segments, ReflowLink switches to that once it has seen segment numbers above the step count a few times. It remembers this after a run started from ReflowLink, so the next runs count steps right from the start (changing the station forgets it).
- **Longest wait:** if a heater has been held for longer than *Settings → Bed Heat Profiles → Longest wait* (10 minutes by default; 0 = never ask), ReflowLink asks whether to keep waiting or stop the program. A heater that never catches up usually means its controller, SSR or thermocouple has a problem.

## Control Tuning

The *Control Tuning* page shows the controller's PID and output settings, explains what each one does, and lets you change them. ReflowLink reads them every time it connects; while offline it shows the factory values of an ACHI IR6500 (P 10, I 61, D 9, output limit 100 %, cycle time 3.0 s, sensor break power 0 %).

- **PID graph:** a simulated heat-up on a generic IR heater model, comparing the controller's current P, I, D and output limit with your edits. Hovering a setting highlights the part of the curve it affects. It shows the direction and rough size of a change, not an exact prediction for your station.
- **Writing:** only changed settings are written, after a confirmation; each one is read back. Writing is locked while a program or auto-tune runs, and until the controller has been read on the current connection.
- **Auto-tune** (`XS`): starts the controller's own tune, with warnings. ReflowLink shows when it's running, blocks program starts meanwhile, and shows the new P, I and D when it ends. It saves the P, I and D from before the tune, and *Restore P, I, D from before auto-tune* writes them back.
- **Advanced:** cutback (`HB`/`LB`), alarms (`HA`/`LA`/`DA`), program repeat count (`Lc`) and ramp-to-setpoint rate (`SR`). ACHI hides these on the front panel; they're editable with an extra warning.

Parameter codes: `XP` (P), `TI` (I), `TD` (D), `HO` (output limit), `CH` (heat cycle time), `BP` (sensor break power). Note that `HB` (high cutback) and `Hb` (holdback) are different parameters.

## What it does

- **Dashboard**: live temperature (PV), setpoint (SP) and heater output, plotted on a chart with the planned profile drawn over it. Click a legend entry to hide or show its curve. Start, hold, resume and stop the program on the controller. Every run is recorded to CSV automatically. The title bar shows *Running* during a run.
- **Stop** is never greyed out by another operation: it cancels whatever ReflowLink is doing (reading or writing a pattern, checking one before a start) and sends Stop to each controller as soon as its serial line is free. A write cut short this way clears and blocks the pattern.
- **Shortcuts**: **F12** stops the program from any page. On the Dashboard, **F5** starts the selected pattern and **F6** holds or resumes. On the Profiles page: Ctrl+S, Ctrl+Shift+S, Ctrl+O, Ctrl+N.
- **Profiles**: an 8-step editor (ramp °C/s → target °C → hold s) with a live preview, total time, peak temperature, time above 183 °C and 217 °C, and the steepest ramp (flagged above 3 °C/s). Profiles are saved as `.rlprofile` files (profiles saved by IR6500 Studio as `.ir6profile` still open), with notes of your own (*Notes*). You can read or write any of the controller's 10 patterns (0–9) with read-back verification. A controller that counts ramps per minute gets ramp values with 4 decimals.
- **Patterns…**: an overview of the 10 patterns. *Read all* reads every pattern (and puts the selected one back), then you can load one into the editor, compare one with the editor, or write a profile file to one.
- **Overlay run…** (Profiles preview): lays a recorded CSV run over the planned curve, so you can see how the station followed the profile.
- **Console**: the raw serial traffic (TX/RX, decoded and in hex), plus manual read or write of any parameter for troubleshooting.
- **Settings**: station, COM port, baud rate, address, timeouts, DTR/RTS, temperature limit, logging folder and CSV format, and a **built-in simulator** so you can try everything without a station. *About → Copy diagnostics* copies the version, setup, connection state and recent events for a support request.

### Updates

When it starts, ReflowLink asks GitHub at most once a day whether a newer release of [ReflowLink](https://github.com/Sumutiu/ReflowLink/releases) is out. It never checks during a run, and without internet it says nothing. If there is a newer release, it shows what's new and offers to open the download page, skip that version, or remind you later. Nothing is downloaded or installed automatically, and only GitHub's public release information is read. *Settings → About* has *Check for updates* and a switch to turn the start-up check off.

Releases are tagged `v<major>.<minor>.<patch>` (for example `v2.1.2`). The check compares the tag with the first three numbers of the exe's version (2.1.2.0), so set *Package version*, *Assembly version* and *File version* in the project to the release number before building it.

### Recordings (CSV)

Each run gets its own file (two runs started in the same second get a suffix, so nothing is overwritten). The file starts with `# ` lines that describe the run (station, units, bed mode, pattern and its steps), followed by the column names and one row per reading. Every row is written to disk straight away. By default the file uses commas and decimal points; *Settings → Logging → CSV format → This PC's format* uses the PC's list separator and decimal mark instead, so Excel opens it in columns where the decimal mark is a comma.

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
              presets, planned curve), AppSettings, CsvRecorder, RecordedRun (CSV overlay), SafeFile
  Services/   DeviceService (connection + background polling), SyncCoordinator (Wait for Bed / IR),
              BedSwitchController (single PC410 bed: IN2 + OUT2), AppLog (event log, crash reports)
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
- **Parameters used:** `PV` `SP` `OP` (telemetry), `OS` (program: `>0000` stop, `>0002` run, `>0003` hold), `SW` (status), `SE` (segment), `ch` (pattern 0–9), `Hb` (holdback), and `r1–r8` / `l1–l8` / `t1–t8` (ramp, target °C, hold, in the controller's units). With a single PC410 bed: `P2` (IN2) and `A2` (alarm 2: `001` on, `500` off).
- **Unused steps:** by default they are written as `0/0/0`. You can choose to write `END` on the first unused ramp instead (in Settings).

## Troubleshooting

| Symptom | Check |
|---|---|
| "The controller did not answer" | COM port, baud rate (must match the controller), address, cable. The format must be 7E1. |
| Device Manager: "PL2303TA does not support Windows 11 or later" | Install an older Prolific driver (3.9.0.2 or 3.8.x) via *Update driver → Browse → Let me pick*, and stop Windows Update replacing it. An FTDI-based USB-RS232 adapter avoids the problem. |
| "Port is in use" | Close any other station software or terminal program that has the COM port open. |
| "Serial port error" during a run | The USB adapter was unplugged or its driver failed. Plug it back in, then disconnect and connect again. The controller keeps running its program meanwhile. |
| "Still waiting" | A Wait for Bed / Wait for IR hold has lasted longer than the *Longest wait* setting. Check the heater that's behind. |
| Works with other software but not here | Compare its baud rate and address. Try toggling DTR/RTS (some isolated adapters take power from them). |
| A profile runs 60× too fast or too slow | The station's units don't match the controller. Choose the right station, or for *Other PC410 / AL808 station* switch the ramp and hold units. |
| Values read back slightly different after writing | The controller rounds some values. The warning dialog lists each difference. |
| "Profile rejected by the controller" or a target "above the controller's setpoint limit" | The target is above the controller's `HS`. Lower it, or raise `HS` in the controller's configuration (and ReflowLink's limit in Settings). |

## Files and folders

- Settings: `%APPDATA%\ReflowLink\settings.xml`. Every save writes a new file and then swaps it in, keeping the previous one as `settings.xml.bak`. If the settings can't be read, the backup is used (or the defaults), and ReflowLink says so when it starts.
- Update check: asks `api.github.com` for the latest release (at most once a day, if switched on).
- Event log and crash reports: `%APPDATA%\ReflowLink\Logs` (one `events-<date>.log` per day, kept 30 days). If ReflowLink closes unexpectedly, the next start says where the crash report is.
- A pattern being written when ReflowLink closes, crashes or loses power is blocked at the next start, until a profile is written to it again.
- Profiles (default): `Documents\ReflowLink\Profiles`
- CSV logs (default): `Documents\ReflowLink\Logs`

## Safety

The station heats to soldering temperatures. The controller keeps running a program even if this software closes or loses the connection. Never leave a run unattended. Verify profiles with an external thermocouple before using them on valuable boards.
