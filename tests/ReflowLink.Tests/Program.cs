// ReflowLink — self tests
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ReflowLink.Model;
using ReflowLink.Protocol;

namespace ReflowLink.Tests {
    internal static class Program {
        private static int _pass, _fail;

        private static int Main() {
            Run("Address encoding doubles each digit", () => {
                Eq("5533", Ascii(Pc410Protocol.EncodeAddress(53)));
                Eq("0011", Ascii(Pc410Protocol.EncodeAddress(1)));
            });

            Run("Read frame matches documented example (PV @ 53)", () =>
                Eq("04 35 35 33 33 50 56 05", Hex(Pc410Protocol.BuildReadFrame(53, "PV"))));

            Run("Read response + BCC match documented example", () => {
                var r = Pc410Protocol.BuildReadResponse("PV", "24.");
                Eq("02 50 56 32 34 2E 03 2D", Hex(r));
                string data = Pc410Protocol.ParseReadResponse(r, 0, r.Length, "PV");
                Eq("24.", data);
                True(Pc410Protocol.TryParseNumber(data, out double v) && Math.Abs(v - 24.0) < 1e-9, "24. parses as 24.0");
            });

            Run("Corrupted checksum is rejected", () => {
                var r = Pc410Protocol.BuildReadResponse("PV", "24.");
                r[r.Length - 1] ^= 0x01;
                Throws<Pc410ChecksumException>(() => Pc410Protocol.ParseReadResponse(r, 0, r.Length, "PV"));
            });

            Run("Write frame layout and checksum", () => {
                var f = Pc410Protocol.BuildWriteFrame(1, "OS", ">0002");
                Eq("<EOT>0011<STX>OS>0002<ETX>[" + f[f.Length - 1].ToString("X2") + "]", Pc410Protocol.Describe(f, 0, f.Length));
                byte bcc = 0;
                for (int i = 6; i < f.Length - 1; i++) bcc ^= f[i];
                Eq(bcc, f[f.Length - 1]);
            });

            Run("Frame finder skips garbage and waits for BCC", () => {
                var resp = Pc410Protocol.BuildReadResponse("SP", "150.0");
                var buf = new byte[64];
                buf[0] = 0x7F; buf[1] = 0x00;
                Array.Copy(resp, 0, buf, 2, resp.Length);
                Eq(-1, Pc410Protocol.FindCompleteReadResponse(buf, 2 + resp.Length - 1, out _));
                Eq(resp.Length, Pc410Protocol.FindCompleteReadResponse(buf, 2 + resp.Length, out int skip));
                Eq(2, skip);
            });

            Run("Number formatting", () => {
                Eq("1.5", Pc410Protocol.FormatNumber(1.5));
                Eq("150", Pc410Protocol.FormatNumber(150.0));
                Eq("1.25", Pc410Protocol.FormatNumber(1.25));
                Eq("0.33", Pc410Protocol.FormatNumber(0.3333));
                Eq("217.5", Pc410Protocol.FormatNumber(217.46, 1));
                Eq("-5", Pc410Protocol.FormatNumber(-5));
                Eq(">0002", Pc410Protocol.FormatHexWord(2));
                Eq(">ABCD", Pc410Protocol.FormatHexWord(0xABCD));
                True(Pc410Protocol.TryParseHexWord(">00ff", out int w) && w == 255, "hex word parse");
                True(Pc410Protocol.IsEnd(" end "), "END detection");
            });

            Run("OS word decoding", () => {
                Eq(ProgramState.Idle, Pc410Device.DecodeOs(0));
                Eq(ProgramState.Running, Pc410Device.DecodeOs(2));
                Eq(ProgramState.Hold, Pc410Device.DecodeOs(3));
                Eq(ProgramState.Hold, Pc410Device.DecodeOs(4));
            });

            Run("Client reads PV from simulator", () => {
                using var c = NewSim(out _);
                double pv = c.ReadNumber("PV");
                True(pv is > 15 and < 35, "ambient PV " + pv);
            });

            Run("Wrong address gives a timeout (silence)", () => {
                var sim = new SimulatedPc410(5);
                sim.Open();
                using var c = new Pc410Client(sim, 6) { ResponseTimeoutMs = 60, Retries = 0 };
                Throws<Pc410TimeoutException>(() => c.Read("PV"));
            });

            Run("Writing a read-only parameter is NAKed", () => {
                using var c = NewSim(out _);
                Throws<Pc410NakException>(() => c.Write("PV", "100"));
            });

            Run("RS485 echo is ignored", () => {
                var sim = new SimulatedPc410(1);
                var echo = new EchoTransport(sim);
                echo.Open();
                using var c = new Pc410Client(echo, 1) { ResponseTimeoutMs = 200, Retries = 0 };
                True(c.ReadNumber("PV") > 0, "read through echo");
                c.WriteNumber("SL", 50);
                Eq(50.0, c.ReadNumber("SL"));
            });

            Run("NAK reply to a read fails fast without retries", () => {
                var t = new ScriptedTransport(Pc410Protocol.NAK);
                t.Open();
                using var c = new Pc410Client(t, 1) { ResponseTimeoutMs = 400, Retries = 3 };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Throws<Pc410NakException>(() => c.Read("XX"));
                True(sw.ElapsedMilliseconds < 300, "took " + sw.ElapsedMilliseconds + " ms");
                Eq(1, t.Writes);
            });

            Run("Optional SE survives a single glitch", () => {
                using var c = NewSim(out var sim);
                var dev = new Pc410Device(c);
                dev.Start(0);
                Thread.Sleep(50);
                True(dev.ReadTelemetry().Segment.HasValue, "segment read");
                sim.Address = 2;                       // controller goes silent for one cycle
                try { dev.ReadTelemetry(); } catch (Pc410Exception) { }
                sim.Address = 1;
                True(dev.ReadTelemetry().Segment.HasValue, "segment still polled after glitch");
                dev.Stop();
            });

            Run("Profile upload / download round trip with verify", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                var p = new ReflowProfile { Holdback = 5 };
                p.Steps.Add(new ProfileStep(1.25, 120, 30));
                p.Steps.Add(new ProfileStep(0.8, 183.5, 45));
                p.Steps.Add(new ProfileStep(2, 215, 10));
                var res = dev.UploadProfile(p, 4, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                Eq(0, res.Warnings.Count);
                Eq(4, dev.ReadPattern());

                dev.SelectPattern(0);
                var back = dev.DownloadProfile(4, null, CancellationToken.None);
                Eq(3, back.Steps.Count);
                Eq(1.25, back.Steps[0].RampRate);
                Eq(183.5, back.Steps[1].Target);
                Eq(10, back.Steps[2].HoldSeconds);
                Eq(5.0, back.Holdback);
            });

            Run("END marker mode terminates the program", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 100, 5));
                dev.UploadProfile(p, 7, UnusedStepMode.EndMarker, true, null, CancellationToken.None);
                Eq("END", c.Read("r2"));
                Eq(1, dev.DownloadProfile(7, null, CancellationToken.None).Steps.Count);
            });

            Run("Pattern cannot change while a program runs", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                dev.Start(0);
                Eq(ProgramState.Running, dev.ReadState());
                Throws<Pc410Exception>(() => dev.SelectPattern(3));
                dev.Hold();
                Eq(ProgramState.Hold, dev.ReadState());
                dev.Resume();
                Eq(ProgramState.Running, dev.ReadState());
                dev.Stop();
                Eq(ProgramState.Idle, dev.ReadState());
            });

            Run("Simulated run follows the profile and finishes", () => {
                using var c = NewSim(out var sim);
                sim.Speed = 50;
                var dev = new Pc410Device(c);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(2, 100, 10));
                p.Steps.Add(new ProfileStep(1, 150, 10));
                dev.UploadProfile(p, 1, UnusedStepMode.Zeros, false, null, CancellationToken.None);
                dev.Start(1);
                double maxPv = 0, maxSp = 0;
                var seenSeg = new HashSet<int>();
                var deadline = DateTime.Now.AddSeconds(15);
                ProgramState st = ProgramState.Running;
                while (DateTime.Now < deadline) {
                    var t = dev.ReadTelemetry();
                    maxPv = Math.Max(maxPv, t.Pv ?? 0);
                    maxSp = Math.Max(maxSp, t.Sp ?? 0);
                    if (t.Segment.HasValue) seenSeg.Add(t.Segment.Value);
                    st = t.State;
                    if (st == ProgramState.Idle) break;
                    Thread.Sleep(50);
                }
                Eq(ProgramState.Idle, st);
                True(maxSp >= 149.9, "setpoint reached 150, got " + maxSp);
                True(maxPv > 140, "PV followed, max " + maxPv);
                True(seenSeg.Contains(1) && seenSeg.Contains(2), "segments 1 and 2 reported");
            });

            Run("Unit conversion: per-minute ramp, minute dwell", () => {
                var u = ControllerUnits.MinutesBased;
                Eq(36.0, u.RampToNative(0.6));
                Eq(0.6, u.RampFromNative(36));
                Eq(0.5, u.DwellToNative(30));
                Eq(90, u.DwellFromNative(1.5));
                var sec = ControllerUnits.SecondsBased;
                Eq(0.6, sec.RampToNative(0.6));
                Eq(45, sec.DwellFromNative(45));
            });

            Run("Profile round trip on a minutes-based controller", () => {
                var sim = new SimulatedPc410(1, 1, ControllerUnits.MinutesBased);
                sim.Open();
                using var c = new Pc410Client(sim, 1) { ResponseTimeoutMs = 150, Retries = 1 };
                var dev = new Pc410Device(c) { Units = ControllerUnits.MinutesBased };
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(0.6, 150, 30));
                p.Steps.Add(new ProfileStep(1.25, 200, 90));
                var res = dev.UploadProfile(p, 2, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                Eq(0, res.Warnings.Count);
                Eq("36", c.Read("r1"));
                Eq("0.5", c.Read("t1"));
                Eq("75", c.Read("r2"));
                Eq("1.5", c.Read("t2"));
                var back = dev.DownloadProfile(2, null, CancellationToken.None);
                Eq(2, back.Steps.Count);
                Eq(0.6, back.Steps[0].RampRate);
                Eq(30, back.Steps[0].HoldSeconds);
                Eq(1.25, back.Steps[1].RampRate);
                Eq(90, back.Steps[1].HoldSeconds);
            });

            Run("Minutes-based simulator runs a profile at the right speed", () => {
                var sim = new SimulatedPc410(1, 50, ControllerUnits.MinutesBased);
                sim.Open();
                using var c = new Pc410Client(sim, 1) { ResponseTimeoutMs = 150, Retries = 1 };
                var dev = new Pc410Device(c) { Units = ControllerUnits.MinutesBased };
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(2, 100, 10));
                dev.UploadProfile(p, 1, UnusedStepMode.Zeros, false, null, CancellationToken.None);
                dev.Start(1);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ProgramState st = ProgramState.Running;
                double maxSp = 0;
                while (sw.ElapsedMilliseconds < 10000) {
                    var t = dev.ReadTelemetry();
                    maxSp = Math.Max(maxSp, t.Sp ?? 0);
                    st = t.State;
                    if (st == ProgramState.Idle) break;
                    Thread.Sleep(40);
                }
                Eq(ProgramState.Idle, st);
                True(maxSp >= 99.9, "setpoint reached 100, got " + maxSp);
                // ~38 s of process time at 50x speed: well under 10 s of wall time, but not instant.
                True(sw.ElapsedMilliseconds > 300, "program finished suspiciously fast");
            });

            Run("Station catalog and settings", () => {
                Eq("achi-ir6500", StationCatalog.Find("unknown-id").Id);
                Eq(230.0, StationCatalog.Find("achi-ir6000").MaxTemperature);
                var s = new AppSettings();
                False(s.StationChosen, "no station before first run");
                s.SelectStation(StationCatalog.GenericId);
                Eq(250.0, s.MaxTemperature);
                s.GenericRampUnit = RampUnit.PerSecond;
                s.GenericDwellUnit = DwellUnit.Minutes;
                Eq(RampUnit.PerSecond, s.Units.Ramp);
                Eq(DwellUnit.Minutes, s.Units.Dwell);
                s.SelectStation("achi-ir-pro-sc");
                Eq(DwellUnit.Seconds, s.Units.Dwell);
                Eq(230.0, s.MaxTemperature);
            });

            Run("Planned curve timing", () => {
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 125, 30));  // 100 s ramp + 30 s
                p.Steps.Add(new ProfileStep(0.5, 150, 0)); // 50 s ramp
                var c = ProfileMath.BuildPlannedCurve(p, 25);
                Eq(180.0, c.TotalSeconds);
                Eq(150.0, c.PeakTemp);
                Eq(2, c.StepStarts.Count);
                Eq("3:00", ProfileMath.FormatDuration(180));
            });

            Run("Profile validation", () => {
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(0, 250, 10));
                var errors = p.Validate(230).ToList();
                True(errors.Any(e => e.Contains("ramp")), "ramp error");
                True(errors.Any(e => e.Contains("230")), "limit error");
            });

            Run("Profile file save / load", () => {
                var p = ProfilePresets.All[1].Create();
                p.Holdback = 3.5;
                string path = Path.Combine(Path.GetTempPath(), "ir6500studio_test" + ReflowProfile.FileExtension);
                p.Save(path);
                var q = ReflowProfile.Load(path);
                File.Delete(path);
                Eq(p.Name, q.Name);
                Eq(p.Steps.Count, q.Steps.Count);
                Eq(3.5, q.Holdback);
                Eq(p.Steps[3].Target, q.Steps[3].Target);
            });

            Console.WriteLine();
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ helpers

        private static Pc410Client NewSim(out SimulatedPc410 sim) {
            sim = new SimulatedPc410(1);
            sim.Open();
            return new Pc410Client(sim, 1) { ResponseTimeoutMs = 150, Retries = 1 };
        }

        /// <summary>Answers every request with the same fixed bytes.</summary>
        private sealed class ScriptedTransport(params byte[] reply) : ITransport {
            private readonly byte[] _reply = reply;
            private readonly Queue<byte> _q = new();
            public int Writes;

            public string Name => "scripted";
            public bool IsOpen { get; private set; }
            public void Open() => IsOpen = true;
            public void Close() => IsOpen = false;
            public void DiscardInput() => _q.Clear();
            public void Write(byte[] data) { Writes++; foreach (var b in _reply) _q.Enqueue(b); }
            public int ReadByte(int t) { if (_q.Count > 0) return _q.Dequeue(); Thread.Sleep(t); return -1; }
            public void Dispose() { }
        }

        private sealed class EchoTransport(ITransport inner) : ITransport {
            private readonly ITransport _inner = inner;
            private readonly Queue<byte> _echo = new();

            public string Name => "echo";
            public bool IsOpen => _inner.IsOpen;
            public void Open() => _inner.Open();
            public void Close() => _inner.Close();
            public void DiscardInput() { _echo.Clear(); _inner.DiscardInput(); }
            public void Write(byte[] data) { foreach (var b in data) _echo.Enqueue(b); _inner.Write(data); }
            public int ReadByte(int t) => _echo.Count > 0 ? _echo.Dequeue() : _inner.ReadByte(t);
            public void Dispose() => _inner.Dispose();
        }

        private static void Run(string name, Action test) {
            try {
                test();
                _pass++;
                Console.WriteLine("PASS  " + name);
            } catch (Exception ex) {
                _fail++;
                Console.WriteLine("FAIL  " + name + " — " + ex.Message);
            }
        }

        private static void Eq<T>(T expected, T actual) {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"expected <{expected}> but got <{actual}>");
        }

        private static void Eq(double expected, double? actual) {
            if (!actual.HasValue || Math.Abs(expected - actual.Value) > 1e-6)
                throw new Exception($"expected <{expected}> but got <{actual}>");
        }

        private static void False(bool cond, string what) => True(!cond, what);

        private static void True(bool cond, string what) {
            if (!cond) throw new Exception("assertion failed: " + what);
        }

        private static void Throws<TEx>(Action a) where TEx : Exception {
            try { a(); } catch (TEx) { return; } catch (Exception ex) { throw new Exception($"expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new Exception($"expected {typeof(TEx).Name}, nothing thrown");
        }

        private static string Ascii(byte[] b) => System.Text.Encoding.ASCII.GetString(b);
        private static string Hex(byte[] b) => Pc410Protocol.ToHex(b, 0, b.Length);
    }
}
