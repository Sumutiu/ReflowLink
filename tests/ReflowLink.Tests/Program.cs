// ReflowLink — self tests
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;

namespace ReflowLink.Tests {
    internal static class Program {
        private static int _pass, _fail;

        private static int Main() {
            AppLog.Disabled = true; // keep the tests out of the real event log
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

            Run("Holdback is one setting for all patterns", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                var p = new ReflowProfile { Holdback = 10 };
                p.Steps.Add(new ProfileStep(1, 100, 5));
                dev.UploadProfile(p, 0, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                p.Holdback = 15;
                dev.UploadProfile(p, 1, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                dev.SelectPattern(0);
                Eq(15.0, dev.ReadHoldback());
                Eq(20.0, dev.WriteHoldback(20));
                Eq(20.0, dev.ReadHoldback());
            });

            Run("A holdback the controller refuses leaves the pattern untouched", () => {
                using var c = NewSim(out var sim);
                var dev = new Pc410Device(c);
                var good = new ReflowProfile { Holdback = 5 };
                good.Steps.Add(new ProfileStep(1, 100, 5));
                dev.UploadProfile(good, 3, UnusedStepMode.Zeros, true, null, CancellationToken.None);

                Catch<ArgumentOutOfRangeException>(() => dev.WriteHoldback(0));
                sim.HoldbackMin = 3; // a controller with a higher minimum than ReflowLink's
                Catch<HoldbackRejectedException>(() => dev.WriteHoldback(2));
                var bad = new ReflowProfile { Holdback = 2 };
                bad.Steps.Add(new ProfileStep(2, 150, 30));
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(bad, 3, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                True(!ex.PatternChanged, "pattern must not count as changed");
                Eq(5.0, dev.ReadHoldback());
                Eq(100.0, dev.DownloadProfile(3, null, CancellationToken.None).Steps[0].Target);
            });

            Run("Control settings: catalog is consistent", () => {
                Eq(TuningCatalog.All.Length, TuningCatalog.All.Select(p => p.Mnemonic).Distinct(StringComparer.Ordinal).Count());
                foreach (var p in TuningCatalog.All) True(p.InRange(p.Factory), p.Mnemonic + " factory value in range");
                True(TuningCatalog.Find("HB") != TuningCatalog.Find("Hb"), "HB (cutback) is not Hb (holdback)");
                Eq(null, TuningCatalog.Find("Hb"));
            });

            Run("Control settings are read, written and checked", () => {
                using var c = NewSim(out var sim);
                var dev = new Pc410Device(c);
                var snap = dev.ReadTuning(null, CancellationToken.None);
                Eq(10.0, snap[TuningCatalog.Proportional]);
                Eq(61.0, snap[TuningCatalog.Integral]);
                Eq(9.0, snap[TuningCatalog.Derivative]);
                Eq(3.0, snap[TuningCatalog.CycleTime]);
                Eq(false, snap.AutoTune == true);
                Eq(12.0, dev.WriteSetting(TuningCatalog.Proportional, 12));
                Eq(13.0, dev.WriteSetting(TuningCatalog.Proportional, 12.6)); // rounded to whole degrees, like the controller
                Eq(13.0, dev.ReadTuning(null, CancellationToken.None)[TuningCatalog.Proportional]);
                Catch<ArgumentOutOfRangeException>(() => dev.WriteSetting(TuningCatalog.SensorBreakPower, 150));
                sim.RefusedWrites.Add("TD");
                var ex = Catch<SettingRejectedException>(() => dev.WriteSetting(TuningCatalog.Derivative, 20));
                True(ex.Message.Contains("refused"), ex.Message);
                Eq(9.0, dev.ReadTuning(null, CancellationToken.None)[TuningCatalog.Derivative]);
                dev.Start(0);
                Catch<Pc410Exception>(() => dev.WriteSetting(TuningCatalog.Integral, 80));
                dev.Stop();
            });

            Run("Control settings a controller doesn't have cost one timeout only", () => {
                using var c = NewSim(out var sim);
                sim.MissingParameters.Add("SR");
                sim.MissingParameters.Add(TuningCatalog.AutoTune);
                var dev = new Pc410Device(c);
                var first = dev.ReadTuning(null, CancellationToken.None);
                Eq(null, first[TuningCatalog.SetpointRate]);
                Eq(null, first.AutoTune);
                Eq(10.0, first[TuningCatalog.Proportional]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                dev.ReadTuning(null, CancellationToken.None);
                True(sw.ElapsedMilliseconds < 250, "second read skips the missing parameters: " + sw.ElapsedMilliseconds + " ms");
                dev.Start(0); // no auto-tune parameter: starting isn't blocked
                dev.Stop();
            });

            Run("Auto-tune starts, blocks programs and finishes", () => {
                using var c = NewSim(out var sim);
                sim.AutoTuneSeconds = 0.3;
                var dev = new Pc410Device(c);
                Eq(false, dev.ReadAutoTune() == true);
                dev.SetAutoTune(true);
                Eq(true, dev.ReadAutoTune() == true);
                var ex = Catch<Pc410Exception>(() => dev.Start(0));
                True(ex.Message.Contains("Auto-tune"), ex.Message);
                ex = Catch<Pc410Exception>(() => dev.WriteSetting(TuningCatalog.Derivative, 12));
                True(ex.Message.Contains("Auto-tune"), ex.Message);
                Thread.Sleep(600);
                Eq(false, dev.ReadAutoTune() == true);
                Eq(12.0, dev.ReadTuning(null, CancellationToken.None)[TuningCatalog.Proportional]);
                dev.Start(0);
                Catch<Pc410Exception>(() => dev.SetAutoTune(true));
                dev.Stop();
            });

            Run("PID simulation reacts to P, I, D and the output limit", () => {
                var factory = PidSimulator.Run(10, 61, 9, 100, 1, 150);
                True(factory.SettleSeconds.HasValue, "factory values settle");
                True(factory.Overshoot < 10, "factory overshoot is moderate: " + factory.Overshoot);
                var tightP = PidSimulator.Run(2, 61, 9, 100, 1, 150);
                True(tightP.Swings > factory.Swings, "small P swings more");
                var wideP = PidSimulator.Run(40, 61, 9, 100, 1, 150);
                True(wideP.RampLag > factory.RampLag, "large P lags more");
                var longI = PidSimulator.Run(10, 600, 9, 100, 1, 150);
                True(longI.SettleSeconds == null || longI.SettleSeconds > factory.SettleSeconds, "long I settles later");
                var noD = PidSimulator.Run(10, 61, 1, 100, 1, 150);
                True(noD.Overshoot > factory.Overshoot, "less D overshoots more");
                var limited = PidSimulator.Run(10, 61, 9, 40, 1, 150);
                True(limited.RampLag > factory.RampLag + 5, "a low output limit can't follow the ramp");
            });

            Run("Bed heat: old profiles read the bed target as the IR target", () => {
                string path = Path.Combine(Path.GetTempPath(), "reflowlink_old" + ReflowProfile.FileExtension);
                File.WriteAllText(path, "<?xml version=\"1.0\"?><ReflowProfile version=\"1\"><Name>Old</Name><Holdback>5</Holdback>"
                    + "<Steps><Step ramp=\"1\" target=\"150\" hold=\"30\" /><Step ramp=\"0.5\" target=\"230\" hold=\"20\" /></Steps></ReflowProfile>");
                var p = ReflowProfile.Load(path);
                File.Delete(path);
                Eq(150.0, p.Steps[0].BedTarget);
                Eq(230.0, p.Steps[1].BedTarget);
                False(p.Steps[0].WaitForBed || p.Steps[0].WaitForIr, "no waits in old files");
                Eq(null, p.BedHoldback);
            });

            Run("Bed heat: profile fields round-trip and the bed copy uses bed targets", () => {
                var p = new ReflowProfile { Name = "Pair", Holdback = 5, BedHoldback = 8 };
                p.Steps.Add(new ProfileStep(1, 150, 30) { BedTarget = 120, WaitForBed = true });
                p.Steps.Add(new ProfileStep(0.5, 230, 20) { BedTarget = 180, WaitForIr = true });
                string path = Path.Combine(Path.GetTempPath(), "reflowlink_pair" + ReflowProfile.FileExtension);
                p.Save(path);
                var q = ReflowProfile.Load(path);
                File.Delete(path);
                Eq(120.0, q.Steps[0].BedTarget);
                Eq(180.0, q.Steps[1].BedTarget);
                True(q.Steps[0].WaitForBed && !q.Steps[0].WaitForIr, "step 1 waits for bed");
                True(q.Steps[1].WaitForIr && !q.Steps[1].WaitForBed, "step 2 waits for IR");
                Eq(8.0, q.BedHoldback);
                var bed = q.ForBed();
                Eq(180.0, bed.Steps[1].Target);
                Eq(0.5, bed.Steps[1].RampRate);
                Eq(20, bed.Steps[1].HoldSeconds);
                Eq(8.0, bed.Holdback);
                var errors = q.Validate(260, 150).ToList();
                True(errors.Any(e => e.Contains("bed target 180")), "bed limit checked: " + string.Join(" | ", errors));
            });

            Run("Bed heat: Wait for Bed / Wait for IR choices are remembered per pattern", () => {
                var p = new ReflowProfile { Holdback = 5, BedHoldback = 7 };
                p.Steps.Add(new ProfileStep(1, 150, 30) { WaitForBed = true });
                p.Steps.Add(new ProfileStep(1, 200, 30) { WaitForIr = true });
                p.Steps.Add(new ProfileStep(1, 230, 30) { WaitForBed = true, WaitForIr = true });
                var sync = PatternSync.From(4, p);
                True(sync.WaitForBed(1) && !sync.WaitForBed(2) && sync.WaitForBed(3), "wait for bed mask");
                True(!sync.WaitForIr(1) && sync.WaitForIr(2) && sync.WaitForIr(3), "wait for IR mask");
                Eq(7.0, sync.BedHoldback);
                var s = new AppSettings();
                s.SetSync(4, p);
                s.SetSync(4, p);
                Eq(1, s.PatternSyncs.Count);
                var read = new ReflowProfile();
                for (int i = 0; i < 3; i++) read.Steps.Add(new ProfileStep(1, 100, 10));
                s.SyncFor(4).ApplyTo(read);
                True(read.Steps[2].WaitForBed && read.Steps[2].WaitForIr && read.Steps[0].WaitForBed && !read.Steps[0].WaitForIr, "applied to a read profile");
            });

            Run("Bed heat: the coordinator holds whichever heater is ahead", () => {
                var p = new ReflowProfile { Holdback = 5, BedHoldback = 5 };
                p.Steps.Add(new ProfileStep(1, 150, 30) { WaitForBed = true });
                p.Steps.Add(new ProfileStep(1, 200, 30) { WaitForIr = true });
                p.Steps.Add(new ProfileStep(1, 230, 30));
                p.Steps.Add(new ProfileStep(1, 230, 30) { WaitForBed = true, WaitForIr = true });
                var sync = PatternSync.From(0, p);
                SyncSide Side(ProgramState st, double pv, double sp, int? step) => new(st, pv, sp, step);
                var run = ProgramState.Running;
                // Step 1 has Wait for Bed: the bed 10 °C behind its setpoint holds the IR.
                Eq((true, false), SyncCoordinator.Decide(sync, Side(run, 140, 140, 1), Side(run, 100, 110, 1)));
                // Within the bed holdback: nobody waits.
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 140, 140, 1), Side(run, 107, 110, 1)));
                // The IR finished step 1 (Wait for Bed) first: it waits for the bed to finish step 1 too.
                Eq((true, false), SyncCoordinator.Decide(sync, Side(run, 150, 152, 2), Side(run, 120, 120, 1)));
                // The bed finished step 1 first, but step 1 has no Wait for IR: it carries on.
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 150, 150, 1), Side(run, 120, 120, 2)));
                // Step 2, same step: the IR lagging holds the bed; the bed lagging doesn't hold the IR.
                Eq((false, true), SyncCoordinator.Decide(sync, Side(run, 150, 170, 2), Side(run, 120, 120, 2)));
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 170, 170, 2), Side(run, 100, 120, 2)));
                // Step 3 has no waits.
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 150, 230, 3), Side(run, 100, 150, 3)));
                // The bed finished step 2 (Wait for IR) first: it waits for the IR.
                Eq((false, true), SyncCoordinator.Decide(sync, Side(run, 190, 190, 2), Side(run, 150, 150, 3)));
                // Step 4 has both: whichever lags holds the other.
                Eq((true, false), SyncCoordinator.Decide(sync, Side(run, 230, 230, 4), Side(run, 200, 230, 4)));
                Eq((false, true), SyncCoordinator.Decide(sync, Side(run, 200, 230, 4), Side(run, 230, 230, 4)));
                // One controller finished or stopped: never hold the other.
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 150, 152, 2), Side(ProgramState.Idle, 120, 120, null)));
                // Steps not reported: can't apply per-step choices.
                Eq((false, false), SyncCoordinator.Decide(sync, Side(run, 140, 140, null), Side(run, 100, 110, null)));
                Eq((false, false), SyncCoordinator.Decide(null, Side(run, 140, 140, 1), Side(run, 100, 110, 1)));
                // Segment numbering: plain steps, or ramp + dwell counted separately.
                Eq(3, SyncCoordinator.StepOf(3, 8, false));
                Eq(2, SyncCoordinator.StepOf(4, 3, false));
                Eq(3, SyncCoordinator.StepOf(5, 8, true));
                Eq(null, SyncCoordinator.StepOf(0, 8, false));
            });

            Run("Bed heat: a pattern is written to and read from both controllers", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out _);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                var p = new ReflowProfile { Holdback = 5, BedHoldback = 9 };
                p.Steps.Add(new ProfileStep(1, 150, 30) { BedTarget = 120 });
                p.Steps.Add(new ProfileStep(0.5, 230, 20) { BedTarget = 180 });
                PatternPair.Write(ir, bed, p, 3, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                Eq("230.0", ci.Read("l2"));
                Eq("180.0", cb.Read("l2"));
                Eq(9.0, bed.ReadHoldback());
                var read = PatternPair.Read(ir, bed, 3, null, CancellationToken.None);
                Eq(0, read.Warnings.Count);
                Eq(120.0, read.Profile.Steps[0].BedTarget);
                Eq(230.0, read.Profile.Steps[1].Target);
                Eq(9.0, read.Profile.BedHoldback);
                // A bed pattern with different timing is reported.
                cb.Write("ch", "3");
                cb.Write("t1", "45");
                read = PatternPair.Read(ir, bed, 3, null, CancellationToken.None);
                True(read.Warnings.Count == 1 && read.Warnings[0].Contains("Step 1"), string.Join(" | ", read.Warnings));
            });

            Run("Bed heat: limits of both controllers are checked before anything is written", () => {
                using var ci = NewSim(out _);
                var simBed = new SimulatedPc410(1, 1, null, 150);
                simBed.Open();
                using var cb = new Pc410Client(simBed, 1) { ResponseTimeoutMs = 150, Retries = 1 };
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 200, 10) { BedTarget = 180 });
                var ex = Catch<ProfileRejectedException>(() => PatternPair.Write(ir, bed, p, 2, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                False(ex.PatternChanged, "nothing written");
                True(ex.Issues[0].StartsWith(PatternPair.BedPrefix), ex.Issues[0]);
                ci.Write("ch", "2");
                Eq("0.0", ci.Read("l1")); // the IR pattern wasn't touched either
            });

            Run("Bed heat: a bed refusal after the IR was written blocks the pattern", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out var simBed);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                simBed.RefusedWrites.Add("l2");
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 10));
                p.Steps.Add(new ProfileStep(1, 200, 10));
                var ex = Catch<ProfileRejectedException>(() => PatternPair.Write(ir, bed, p, 5, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                True(ex.PatternChanged, "the pair no longer matches");
                True(ex.Issues[0].StartsWith(PatternPair.BedPrefix), ex.Issues[0]);
            });

            Run("Bed heat: both start together, and the IR is stopped if the bed can't start", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out var simBed);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 600));
                PatternPair.Write(ir, bed, p, 1, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                PatternPair.Check(ir, bed, 1, 260, 260, null, CancellationToken.None);
                PatternPair.Start(ir, bed, 1);
                Eq(ProgramState.Running, ir.ReadState());
                Eq(ProgramState.Running, bed.ReadState());
                PatternPair.Hold(ir, bed);
                Eq(ProgramState.Hold, bed.ReadState());
                PatternPair.Stop(ir, bed);
                Eq(ProgramState.Idle, ir.ReadState());
                Eq(ProgramState.Idle, bed.ReadState());

                simBed.RefusedWrites.Add("OS");
                var ex = Catch<Pc410Exception>(() => PatternPair.Start(ir, bed, 1));
                True(ex.Message.Contains("stopped again"), ex.Message);
                Eq(ProgramState.Idle, ir.ReadState());
            });

            Run("Bed heat: resume never restarts an idle controller, and hold skips idle ones", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out _);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                False(ir.ResumeIfHeld(), "nothing to resume");
                Eq(ProgramState.Idle, ir.ReadState());
                bed.Start(0);
                PatternPair.Hold(ir, bed); // the IR is idle: only the bed is held, without an error
                Eq(ProgramState.Idle, ir.ReadState());
                Eq(ProgramState.Hold, bed.ReadState());
                PatternPair.Resume(ir, bed);
                Eq(ProgramState.Idle, ir.ReadState());
                Eq(ProgramState.Running, bed.ReadState());
                bed.Stop();
            });

            Run("Bed heat: a start is refused when the two patterns differ", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out _);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 30));
                p.Steps.Add(new ProfileStep(1, 180, 30));
                ir.UploadProfile(p, 4, UnusedStepMode.Zeros, true, null, CancellationToken.None); // written with bed heat off
                var one = new ReflowProfile();
                one.Steps.Add(new ProfileStep(1, 150, 30));
                bed.UploadProfile(one, 4, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                var ex = Catch<ProfileRejectedException>(() => PatternPair.Check(ir, bed, 4, 260, 260, null, CancellationToken.None));
                False(ex.PatternChanged, "nothing changed");
                True(ex.Issues.Any(i => i.Contains("step(s)")), string.Join(" | ", ex.Issues));
            });

            Run("Bed heat: nothing is written while either controller runs a program", () => {
                using var ci = NewSim(out _);
                using var cb = NewSim(out _);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                bed.Start(0);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 30));
                Catch<Pc410Exception>(() => PatternPair.Write(ir, bed, p, 6, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                ci.Write("ch", "6");
                Eq("0.0", ci.Read("l1"));
                bed.Stop();
            });

            Run("Bed heat: a failed IR start stops both again", () => {
                using var ci = NewSim(out var simIr);
                using var cb = NewSim(out _);
                var ir = new Pc410Device(ci);
                var bed = new Pc410Device(cb);
                simIr.RefusedWrites.Add("OS");
                var ex = Catch<Pc410Exception>(() => PatternPair.Start(ir, bed, 0));
                True(ex.Message.Contains("Top IR"), ex.Message);
                Eq(ProgramState.Idle, bed.ReadState());
            });

            Run("Bed heat: operations on both connected controllers don't deadlock", () => {
                var ir = new DeviceService();
                var bed = new DeviceService();
                try {
                    var options = new ConnectionOptions { UseSimulator = true, Address = 1, PollIntervalMs = 200 };
                    True(ir.ConnectAsync(options).Wait(10000), "IR connected");
                    True(bed.ConnectAsync(options).Wait(10000), "bed connected");
                    var p = new ReflowProfile { Holdback = 5, BedHoldback = 6 };
                    p.Steps.Add(new ProfileStep(1, 150, 30) { BedTarget = 120 });
                    for (int i = 0; i < 5; i++) {
                        var write = DeviceService.RunBothAsync(ir, bed, (a, b) => PatternPair.Write(a, b, p, 2, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                        True(write.Wait(15000), "pair write finished (attempt " + (i + 1) + ")");
                        var read = DeviceService.RunBothAsync(ir, bed, (a, b) => PatternPair.Read(a, b, 2, null, CancellationToken.None));
                        True(read.Wait(15000), "pair read finished");
                        Eq(120.0, read.Result.Profile.Steps[0].BedTarget);
                    }
                } finally {
                    ir.Dispose();
                    bed.Dispose();
                }
            });

            Run("Bed heat: bed settings are sanitised and give their own connection", () => {
                var s = new AppSettings();
                s.Bed.Address = 300;
                s.Bed.MaxTemperature = -5;
                s.Bed.PortName = "COM7";
                s.Bed.RampUnit = RampUnit.PerMinute;
                s.Sanitize();
                Eq(1, s.Bed.Address);
                Eq(260.0, s.Bed.MaxTemperature);
                var o = s.Bed.Connection();
                Eq("COM7", o.PortName);
                Eq(RampUnit.PerMinute, o.Units.Ramp);
                Eq(DwellUnit.Seconds, o.Units.Dwell);
                False(s.Bed.Enabled, "off by default");
            });

            Run("Single PC410: A2 and P2 frames are byte for byte as expected", () => {
                // EOT "0011" STX "A2001" ETX 'A' switches the bed on, and "A2500" ETX 'E' switches it off.
                Eq("04 30 30 31 31 02 41 32 30 30 31 03 41", Hex(Pc410Protocol.BuildWriteFrame(1, Pc410Device.BedAlarm, Pc410Device.BedOnValue)));
                Eq("04 30 30 31 31 02 41 32 35 30 30 03 45", Hex(Pc410Protocol.BuildWriteFrame(1, Pc410Device.BedAlarm, Pc410Device.BedOffValue)));
                Eq("04 30 30 31 31 50 32 05", Hex(Pc410Protocol.BuildReadFrame(1, Pc410Device.BedInput)));
            });

            Run("Single PC410: bed settings migrate from 2.0.0 and keep the old flag in step", () => {
                var s = new AppSettings();
                s.Bed.Enabled = true; // a 2.0.0 file with "Use Bed Heat Control" on
                s.Sanitize();
                Eq(BedMode.SecondPc410, s.Bed.Mode);
                var t = new AppSettings();
                t.Bed.Mode = BedMode.SinglePc410;
                t.Bed.Switching.CycleSeconds = 1;
                t.Sanitize();
                Eq(BedMode.SinglePc410, t.Bed.Mode);
                False(t.Bed.Enabled, "2.0.0 must not mistake a single PC410 for a second one");
                Eq(4.0, t.Bed.Switching.CycleSeconds);
                Eq(BedMode.None, new AppSettings().Sanitize().Bed.Mode);
            });

            Run("Single PC410: bed targets are kept per pattern and only restored onto the same profile", () => {
                var p = new ReflowProfile { BedHoldback = 7 };
                p.Steps.Add(new ProfileStep(1.0, 120, 30) { BedTarget = 100, WaitForBed = true });
                p.Steps.Add(new ProfileStep(0.5, 160, 40) { BedTarget = 135.5 });
                var sync = PatternSync.From(3, p);
                True(sync.HasTargets, "targets saved");
                Eq("120;160", sync.IrTargetList);
                Eq("100;135.5", sync.BedTargetList);
                // Read back from the controller: no bed targets there.
                var read = new ReflowProfile();
                read.Steps.Add(new ProfileStep(1.0, 120, 30));
                read.Steps.Add(new ProfileStep(0.5, 160, 40));
                True(sync.ApplyTo(read, true), "restored");
                Eq(135.5, read.Steps[1].BedTarget);
                Eq(7.0, read.BedHoldback);
                True(read.Steps[0].WaitForBed, "wait restored");
                // Pattern changed on the front panel: the bed targets don't belong to it any more.
                read.Steps[1].Target = 170;
                False(sync.Matches(read), "changed pattern");
                False(sync.ApplyTo(read, true), "not restored onto a changed pattern");
                False(new PatternSync { Steps = 2 }.HasTargets, "saved before 2.1: no targets");
            });

            Run("Single PC410: the bed setpoint follows the IR program step by step", () => {
                var plan = new BedPlan([120, 160, 205], [100, 130, 150], irStart: 25, bedStart: 25);
                Eq(25.0, plan.SetpointAt(1, 25));
                Eq(62.5, plan.SetpointAt(1, 72.5));   // halfway up the IR ramp, halfway to the bed target
                Eq(100.0, plan.SetpointAt(1, 120));   // IR holding: bed holds its target
                Eq(115.0, plan.SetpointAt(2, 140));
                Eq(150.0, plan.SetpointAt(3, null));  // no IR setpoint reported: the step's bed target
                Eq(100.0, plan.SetpointAt(1, 400));   // never beyond the step's target
                var flat = new BedPlan([120, 120], [100, 140], 25, 25);
                Eq(140.0, flat.SetpointAt(2, 120));   // IR doesn't move in step 2: the bed goes straight to its target
            });

            Run("Single PC410: regulator and time proportioning", () => {
                var r = new BedRegulator { Band = 10, IntegralSeconds = 0 };
                Eq(50.0, r.Update(100, 95, 1));
                Eq(0.0, r.Update(100, 101, 1));
                Eq(100.0, r.Update(100, 70, 1));
                var pi = new BedRegulator { Band = 10, IntegralSeconds = 100 };
                double first = pi.Update(100, 99, 10), later = 0;
                for (int i = 0; i < 20; i++) later = pi.Update(100, 99, 10);
                True(later > first, "the integral builds up a remaining offset");
                var tp = new TimeProportioner { CycleSeconds = 10, MinSwitchSeconds = 1 };
                True(tp.On(0, 40) && tp.On(3.9, 40), "on for the first 40 % of the cycle");
                False(tp.On(4.1, 40) || tp.On(9.9, 40), "off for the rest");
                True(tp.On(10.2, 40), "next cycle starts on");
                var tp2 = new TimeProportioner { CycleSeconds = 10, MinSwitchSeconds = 1 };
                False(tp2.On(0, 5), "a pulse shorter than the minimum is dropped");
                var tp3 = new TimeProportioner { CycleSeconds = 10, MinSwitchSeconds = 1 };
                True(tp3.On(0, 95) && tp3.On(9.6, 95), "a gap shorter than the minimum is filled");
            });

            Run("Single PC410: the bed stays off whenever it can't be regulated", () => {
                var plan = new BedPlan([150], [120], 25, 25);
                Eq("Not connected", BedSwitchController.Decide(false, true, false, plan, 1, 80, 260));
                Eq("Stopped", BedSwitchController.Decide(true, true, true, plan, 1, 80, 260));
                Eq("Idle", BedSwitchController.Decide(true, false, false, plan, 1, 80, 260));
                Eq("No bed targets", BedSwitchController.Decide(true, true, false, null, 1, 80, 260));
                Eq("No bed reading (P2)", BedSwitchController.Decide(true, true, false, plan, 1, null, 260));
                Eq("Step not reported", BedSwitchController.Decide(true, true, false, plan, null, 80, 260));
                Eq("Above 260 °C limit", BedSwitchController.Decide(true, true, false, plan, 1, 261, 260));
                Eq(null, BedSwitchController.Decide(true, true, false, plan, 1, 80, 260));
            });

            Run("Single PC410: simulator gates the bed through OUT2 like a high alarm", () => {
                using var c = NewSim(out var sim);
                sim.BedCeiling = 150;
                False(sim.Out2, "A2 = 500 at power-up: OUT2 open");
                double start = sim.BedTemperature;
                sim.Advance(60);
                True(Math.Abs(sim.BedTemperature - start) < 0.5, "no heat while OUT2 is open");
                c.Write(Pc410Device.BedAlarm, Pc410Device.BedOnValue);
                True(sim.Out2, "A2 = 001: PV above the alarm, OUT2 closed");
                sim.Advance(120);
                True(sim.BedTemperature > start + 40, "bed heats through OUT2");
                sim.Advance(1200);
                True(sim.BedTemperature < 152, "the bed controller behind OUT2 still limits it");
                c.Write(Pc410Device.BedAlarm, Pc410Device.BedOffValue);
                False(sim.Out2, "A2 = 500: OUT2 open again");
                Eq(500.0, c.ReadNumber(Pc410Device.BedAlarm));
                True(c.ReadNumber(Pc410Device.BedInput) > 100, "P2 reads the bed");
            });

            Run("Single PC410: P2 and A2 are polled, and a missing IN2 stops costing time", () => {
                using var c = NewSim(out var sim);
                var dev = new Pc410Device(c);
                var t0 = dev.ReadTelemetry(in2: true, alarm2: true);
                True(t0.Pv2.HasValue && t0.Alarm2 == 500, "P2 and A2 on the first poll");
                var t1 = dev.ReadTelemetry(in2: true, alarm2: true);
                True(t1.Pv2.HasValue && !t1.Alarm2.HasValue, "A2 only every other poll");
                False(dev.ReadTelemetry().Pv2.HasValue, "not read unless asked for");
                sim.HideBedInput = true;
                for (int i = 0; i < 4; i++) dev.ReadTelemetry(in2: true, alarm2: true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var t = dev.ReadTelemetry(in2: true, alarm2: true);
                False(t.Pv2.HasValue, "no bed reading");
                True(sw.ElapsedMilliseconds < 150, "given up after repeated misses");
            });

            Run("Single PC410: ReflowLink switches the bed through OUT2 during a run and off at the end", () => {
                var settings = new AppSettings();
                settings.Bed.Mode = BedMode.SinglePc410;
                settings.Bed.Switching.CycleSeconds = 4;
                var prof = new ReflowProfile();
                prof.Steps.Add(new ProfileStep(1.5, 120, 20) { BedTarget = 110 });
                prof.Steps.Add(new ProfileStep(0.8, 160, 40) { BedTarget = 120 });
                prof.Steps.Add(new ProfileStep(1.0, 205, 20) { BedTarget = 130 });
                settings.SetSync(0, prof); // the simulator's demo pattern 0 has the same IR steps
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, SimulatorSpeed = 10, PollIntervalMs = 200 }).Wait();
                    var sim = (SimulatedPc410)device.RunAsync(d => d.Client.Transport).Result;
                    device.ReadIn2 = device.ReadAlarm2 = true;
                    // A bed left switched on (another program, a crash) is switched off while no program runs.
                    device.RunAsync(d => d.SetBedSwitch(true)).Wait();
                    True(sim.Out2, "left on");
                    var ctl = new BedSwitchController(device, settings) { Enabled = true, KnownPattern = slot => slot == 0 ? prof : null };
                    True(WaitFor(() => !sim.Out2, 5000), "switched off while idle");
                    int writesIdle = ctl.SessionWrites;

                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => ctl.Plan != null, 5000), "the plan saved with pattern 0 is used for a front-panel style start of a checked pattern");
                    True(WaitFor(() => sim.Out2, 8000), "bed switched on while it's below its setpoint");
                    True(WaitFor(() => ctl.Setpoint.HasValue && ctl.BedTemperature.HasValue, 2000), "regulating");
                    Thread.Sleep(1500);
                    True(ctl.SessionWrites - writesIdle < 30, "only state changes are written");

                    ctl.Stop();
                    device.RunAsync(d => d.Stop()).Wait();
                    True(WaitFor(() => !sim.Out2, 5000), "Stop switches the bed off");
                    Thread.Sleep(800);
                    False(sim.Out2, "and it stays off");
                    True(ctl.Reason is "Stopped" or "Idle", "reason: " + ctl.Reason);
                } finally {
                    device.Dispose();
                }
            });

            Run("Single PC410: an unchecked front-panel start and a run already going don't heat the bed", () => {
                var settings = new AppSettings();
                settings.Bed.Mode = BedMode.SinglePc410;
                var prof = new ReflowProfile();
                prof.Steps.Add(new ProfileStep(1.5, 120, 20) { BedTarget = 110 });
                prof.Steps.Add(new ProfileStep(0.8, 160, 40) { BedTarget = 120 });
                prof.Steps.Add(new ProfileStep(1.0, 205, 20) { BedTarget = 130 });
                settings.SetSync(0, prof);
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, SimulatorSpeed = 10, PollIntervalMs = 200 }).Wait();
                    var sim = (SimulatedPc410)device.RunAsync(d => d.Client.Transport).Result;
                    device.ReadIn2 = device.ReadAlarm2 = true;
                    // The pattern wasn't read or written this session: its saved bed targets can't be trusted.
                    var ctl = new BedSwitchController(device, settings) { Enabled = true, KnownPattern = slot => null };
                    Thread.Sleep(600);
                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => ctl.Reason == "Pattern not checked", 5000), "reason: " + ctl.Reason);
                    Thread.Sleep(1000);
                    False(sim.Out2, "bed stays off");
                    Eq(null, ctl.Plan);
                    // Bed control switched on while that run goes on: it waits for the next run.
                    ctl.Enabled = false;
                    ctl.KnownPattern = slot => prof;
                    ctl.Enabled = true;
                    True(WaitFor(() => ctl.Reason == "From the next run", 5000), "reason: " + ctl.Reason);
                    False(sim.Out2, "still off");
                    True(ctl.SwitchOffAsync().Result, "switch-off confirmed by reading A2 back");
                    False(sim.Out2, "off");
                    device.RunAsync(d => d.Stop()).Wait();
                } finally {
                    device.Dispose();
                }
            });

            Run("IN2 readings: polled only where they apply, and logged as information", () => {
                var s = new AppSettings();
                var ir = new DeviceService();
                var bed = new DeviceService();
                var st = new ReflowLink.UI.AppState(s, ir, bed);
                False(ir.ReadIn2 || ir.ReadAlarm2 || bed.ReadIn2, "nothing extra by default");
                s.ReadIn2 = true;
                s.Bed.ReadIn2 = true;
                st.ApplyBedMode();
                True(st.IrIn2Shown && ir.ReadIn2 && !ir.ReadAlarm2, "Top IR only: IN2 (Top IR) is read, A2 isn't");
                False(st.BedIn2Shown || bed.ReadIn2, "no second PC410: IN2 (Bed Heat) can't be on");
                s.Bed.Mode = BedMode.SinglePc410;
                s.ReadIn2 = false;
                st.ApplyBedMode();
                True(ir.ReadIn2 && ir.ReadAlarm2, "single PC410: IN2 and A2 always read, whatever the setting");
                False(st.IrIn2Shown || st.BedIn2Shown, "single PC410: IN2 is the bed temperature, not an extra line");
                s.Bed.Mode = BedMode.SecondPc410;
                st.ApplyBedMode();
                True(st.BedIn2Shown && bed.ReadIn2 && !ir.ReadIn2, "second PC410: its IN2 can be shown");

                string folder = Path.Combine(Path.GetTempPath(), "reflowlink_in2_test");
                using (var rec = new CsvRecorder(folder, "in2", withIrIn2: true)) {
                    rec.Write(new Telemetry { Time = DateTime.Now, Pv = 150, Pv2 = 98.25, State = ProgramState.Running });
                    string path = rec.FilePath;
                    rec.Dispose();
                    var lines = File.ReadAllLines(path);
                    True(lines[0].EndsWith(",in2_top_ir_c"), "header: " + lines[0]);
                    True(lines[1].EndsWith(",98.3") || lines[1].EndsWith(",98.2"), "row: " + lines[1]);
                    File.Delete(path);
                }
            });

            Run("A failing serial port is a controller error, without retries", () => {
                var port = new BrokenTransport();
                using var c = new Pc410Client(port, 1) { ResponseTimeoutMs = 60, Retries = 3 };
                var ex = Catch<Pc410PortException>(() => c.Read("PV"));
                True(ex is Pc410Exception, "handled like any controller error");
                Eq(1, port.Writes);
                Throws<Pc410PortException>(() => c.Write("SP", "100"));
                // Stop on both controllers still reports the failure instead of escaping as an IOException.
                var dev = new Pc410Device(c);
                Throws<Pc410Exception>(() => PatternPair.Stop(dev, null));
            });

            Run("Stop cancels a profile write and blocks the half-written pattern", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 10));
                p.Steps.Add(new ProfileStep(1, 200, 10));
                using var cts = new CancellationTokenSource();
                var progress = new SyncProgress(op => { if (op.Text.StartsWith("Writing step 2")) cts.Cancel(); });
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(p, 5, UnusedStepMode.Zeros, true, progress, cts.Token));
                True(ex.Interrupted && ex.PatternChanged && ex.PatternCleared, "cancelled, cleared and to be blocked");
                True(ex.Issues[0].Contains("cancelled"), ex.Issues[0]);
                Eq(0, dev.DownloadProfile(5, null, CancellationToken.None).Steps.Count);
            });

            Run("Stop never waits behind another operation and cancels it", () => {
                var s = new AppSettings();
                var ir = new DeviceService();
                var bed = new DeviceService();
                try {
                    ir.ConnectAsync(new ConnectionOptions { UseSimulator = true, PollIntervalMs = 200 }).Wait();
                    var st = new ReflowLink.UI.AppState(s, ir, bed);
                    ir.RunAsync(d => d.Start(0)).Wait();
                    var slow = st.RunOnAsync(st.Ir, "Slow read", (d, p, ct) => {
                        for (int i = 0; i < 400; i++) {
                            ct.ThrowIfCancellationRequested();
                            d.Client.Read("PV");
                        }
                        return true;
                    });
                    Thread.Sleep(150);
                    True(st.Busy, "busy with the slow read");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    True(st.StopAllAsync().Result, "stop sent while busy");
                    True(sw.ElapsedMilliseconds < 2000, "no waiting for the slow read: " + sw.ElapsedMilliseconds + " ms");
                    False(slow.Result.ok, "the slow read was cancelled");
                    Eq(ProgramState.Idle, ir.RunAsync(d => d.ReadState()).Result);
                    Eq(1, st.StopCount);
                    False(st.Busy, "idle again");
                } finally {
                    ir.Dispose();
                    bed.Dispose();
                }
            });

            Run("The running pattern can be read; another one only when idle", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                dev.Start(0);
                Eq(3, dev.DownloadProfile(0, null, CancellationToken.None).Steps.Count);
                var ex = Catch<Pc410Exception>(() => dev.DownloadProfile(4, null, CancellationToken.None));
                True(ex.Message.Contains("is running"), ex.Message);
                Eq(ProgramState.Running, dev.ReadState());
                dev.Stop();
                dev.DownloadProfile(4, null, CancellationToken.None);
                // Program state unknown: no pattern switch on a guess.
                c.Transport.Dispose();
                var sim2 = new SimulatedPc410(1);
                sim2.Open();
                using var c2 = new Pc410Client(sim2, 1) { ResponseTimeoutMs = 60, Retries = 0 };
                var dev2 = new Pc410Device(c2);
                sim2.MissingParameters.Add("OS");
                ex = Catch<Pc410Exception>(() => dev2.DownloadProfile(4, null, CancellationToken.None));
                True(ex.Message.Contains("didn't report"), ex.Message);
            });

            Run("A write cut short by closing or a crash blocks the pattern at the next start", () => {
                string folder = Path.Combine(Path.GetTempPath(), "reflowlink_writing_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try {
                    string path = Path.Combine(folder, "settings.xml");
                    new AppSettings { StationId = StationCatalog.DefaultId, WritingPattern = 4 }.SaveTo(path);
                    var s = AppSettings.LoadFrom(path);
                    Eq(4, s.InterruptedWrite);
                    True(s.BlockedPatterns.Contains(4), "blocked");
                    Eq(-1, s.WritingPattern);
                    new AppSettings { StationId = StationCatalog.DefaultId }.SaveTo(path);
                    Eq(null, AppSettings.LoadFrom(path).InterruptedWrite);
                } finally {
                    Directory.Delete(folder, true);
                }
            });

            Run("The step (SE) comes back after the controller stopped reporting it", () => {
                using var c = NewSim(out var sim);
                var dev = new Pc410Device(c);
                dev.Start(0);
                sim.MissingParameters.Add("SE");
                for (int i = 0; i < 4; i++) dev.ReadTelemetry();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                False(dev.ReadTelemetry().Segment.HasValue, "not reported");
                True(sw.ElapsedMilliseconds < 120, "and no longer asked for every poll");
                sim.MissingParameters.Remove("SE");
                bool back = false;
                for (int i = 0; i < 25 && !back; i++) back = dev.ReadTelemetry().Segment.HasValue;
                True(back, "SE read again");
                True(dev.ReadTelemetry().Segment.HasValue, "and every poll after that");
                dev.Stop();
            });

            Run("A dead link on the first HS read doesn't switch the limit check off", () => {
                var sim = new SimulatedPc410(1);
                var flaky = new FlakyTransport(sim);
                flaky.Open();
                using var c = new Pc410Client(flaky, 1) { ResponseTimeoutMs = 60, Retries = 0 };
                var dev = new Pc410Device(c);
                flaky.SilentAfterWrites = 0;
                Throws<Pc410TimeoutException>(() => dev.ReadSetpointLimits());
                flaky.SilentAfterWrites = int.MaxValue;
                Eq(260.0, dev.ReadSetpointLimits().High);
            });

            Run("Settings are saved atomically and a damaged file falls back to the backup", () => {
                string folder = Path.Combine(Path.GetTempPath(), "reflowlink_settings_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try {
                    string path = Path.Combine(folder, "settings.xml");
                    var s = new AppSettings { StationId = StationCatalog.DefaultId, BlockedPatterns = [3] };
                    s.SaveTo(path);
                    s.BlockedPatterns = [3, 7];
                    s.SaveTo(path);
                    True(File.Exists(path + ".bak"), "previous file kept");
                    Eq(2, AppSettings.LoadFrom(path).BlockedPatterns.Count);
                    Eq(null, AppSettings.LoadFrom(path).LoadWarning);

                    File.WriteAllText(path, "<AppSettings><Broken");
                    var fromBackup = AppSettings.LoadFrom(path);
                    True(fromBackup.LoadWarning != null, "the user is told");
                    Eq(1, fromBackup.BlockedPatterns.Count); // the save before
                    False(File.Exists(path), "the damaged file is set aside");
                    True(Directory.GetFiles(folder, "settings.unreadable-*").Length == 1, "and kept for inspection");

                    File.WriteAllText(path + ".bak", "not xml");
                    File.WriteAllText(path, "not xml either");
                    var defaults = AppSettings.LoadFrom(path);
                    True(defaults.LoadWarning != null && defaults.LoadWarning.Contains("default"), "defaults, and the user is told");

                    // A failing write leaves the old file untouched.
                    File.WriteAllText(path, "keep me");
                    Throws<InvalidOperationException>(() => SafeFile.Write(path, st => {
                        st.WriteByte(1);
                        throw new InvalidOperationException("disk full");
                    }));
                    Eq("keep me", File.ReadAllText(path));
                    False(File.Exists(path + ".tmp"), "temporary file removed");
                } finally {
                    Directory.Delete(folder, true);
                }
            });

            Run("CSV recordings: unique names, this PC's format, run details and overlay", () => {
                string folder = Path.Combine(Path.GetTempPath(), "reflowlink_csv_test_" + Guid.NewGuid().ToString("N"));
                var culture = Thread.CurrentThread.CurrentCulture;
                try {
                    var t0 = DateTime.Now;
                    using var a = new CsvRecorder(folder, "run", withBed: true, metadata: ["Station: test", "Pattern: 2"]);
                    using var b = new CsvRecorder(folder, "run", withBed: true);
                    True(a.FilePath != b.FilePath, "two recordings never share a file");
                    a.Write(new Telemetry { Time = t0, Pv = 25, State = ProgramState.Idle }, new Telemetry { Pv = 24 });
                    a.Write(new Telemetry { Time = t0.AddSeconds(2), Pv = 30, State = ProgramState.Running }, new Telemetry { Pv = 26 });
                    a.Write(new Telemetry { Time = t0.AddSeconds(4), Pv = 40.5, State = ProgramState.Running }, new Telemetry { Pv = 28 });
                    a.Dispose();
                    var lines = File.ReadAllLines(a.FilePath);
                    Eq("# Station: test", lines[0]);
                    True(lines[2].StartsWith("timestamp,"), lines[2]);
                    var run = RecordedRun.Load(a.FilePath);
                    Eq(3, run.Ir.Count);
                    Eq(0.0, run.Ir[1].Time); // time 0 is where the program started
                    Eq(40.5, run.Ir[2].Temp);
                    Eq(28.0, run.Bed[2].Temp);

                    Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                    using var c = new CsvRecorder(folder, "local", localFormat: true);
                    c.Write(new Telemetry { Time = t0, Pv = 98.25, State = ProgramState.Running });
                    c.Dispose();
                    var local = File.ReadAllLines(c.FilePath);
                    True(local[0].StartsWith("timestamp;elapsed_s;"), local[0]);
                    True(local[1].Contains(";98,3;") || local[1].Contains(";98,2;"), local[1]);
                    Thread.CurrentThread.CurrentCulture = culture;
                    Eq(98.25, RecordedRun.Load(c.FilePath).Ir[0].Temp, 0.06);
                } finally {
                    Thread.CurrentThread.CurrentCulture = culture;
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
            });

            Run("Profile statistics: time above liquidus and steepest ramp", () => {
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 200, 0));   // 25 → 200 in 175 s
                p.Steps.Add(new ProfileStep(1, 230, 20));  // 200 → 230 in 30 s, hold 20 s
                p.Steps.Add(new ProfileStep(4, 150, 0));   // 230 → 150 in 20 s
                var curve = ProfileMath.BuildPlannedCurve(p, 25);
                // Above 217: 13 s rising, 20 s hold, 3.25 s falling.
                Eq(36.25, ProfileMath.TimeAbove(curve, 217), 0.01);
                Eq(4.0, ProfileMath.SteepestRamp(p));
                Eq(0.0, ProfileMath.TimeAbove(curve, 300));
            });

            Run("Profile files: too many steps and no steps are reported", () => {
                string path = Path.Combine(Path.GetTempPath(), "reflowlink_steps_test.rlp");
                try {
                    var p = new ReflowProfile { Name = "Ten" };
                    for (int i = 0; i < 10; i++) p.Steps.Add(new ProfileStep(1, 100 + i, 5));
                    p.Save(path);
                    var loaded = ReflowProfile.Load(path);
                    Eq(8, loaded.Steps.Count);
                    True(loaded.LoadNote != null && loaded.LoadNote.Contains("10 steps"), loaded.LoadNote ?? "no note");
                    new ReflowProfile { Name = "Empty" }.Save(path);
                    True(ReflowProfile.Load(path).LoadNote != null, "empty file noted");
                    p.Steps.RemoveRange(2, 8);
                    p.Save(path);
                    Eq(null, ReflowProfile.Load(path).LoadNote);
                } finally {
                    File.Delete(path);
                }
            });

            Run("Paired segments are remembered for the next run", () => {
                var ir = new DeviceService();
                var bed = new DeviceService();
                var sync = new SyncCoordinator(ir, bed);
                sync.Reset();
                False(sync.PairedSegments, "unknown at first");
                sync.PairedHint = true;
                sync.Reset();
                True(sync.PairedSegments, "learned earlier: assumed from the start");
                sync.RunStarted();
                True(sync.PairedSegments, "kept for a new run");
                Eq(2, SyncCoordinator.StepOf(3, 5, sync.PairedSegments)); // segment 3 = ramp of step 2

                var s = new AppSettings { StationId = StationCatalog.DefaultId };
                var st = new ReflowLink.UI.AppState(s, ir, bed);
                False(st.Sync.PairedHint, "nothing learned yet");
                s.SegmentsPaired = true;
                s.SelectStation(StationCatalog.GenericId);
                False(s.SegmentsPaired, "forgotten when the station changes");
            });

            Run("Single PC410: switching the bed off before a disconnect keeps the run until it's confirmed", () => {
                var settings = new AppSettings();
                settings.Bed.Mode = BedMode.SinglePc410;
                settings.Bed.Switching.CycleSeconds = 4;
                var prof = new ReflowProfile();
                prof.Steps.Add(new ProfileStep(1.5, 120, 20) { BedTarget = 110 });
                prof.Steps.Add(new ProfileStep(0.8, 160, 40) { BedTarget = 120 });
                prof.Steps.Add(new ProfileStep(1.0, 205, 20) { BedTarget = 130 });
                settings.SetSync(0, prof);
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, SimulatorSpeed = 10, PollIntervalMs = 200 }).Wait();
                    var sim = (SimulatedPc410)device.RunAsync(d => d.Client.Transport).Result;
                    device.ReadIn2 = device.ReadAlarm2 = true;
                    var ctl = new BedSwitchController(device, settings) { Enabled = true, KnownPattern = slot => slot == 0 ? prof : null };
                    Thread.Sleep(600);
                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => sim.Out2, 8000), "heating");
                    True(ctl.SwitchOffAsync().Result, "confirmed off");
                    False(sim.Out2, "OUT2 off");
                    True(ctl.Plan != null, "the run is kept, in case the user cancels");
                    True(WaitFor(() => ctl.Reason == "Switched off", 2000), "reason: " + ctl.Reason);
                    Thread.Sleep(800);
                    False(sim.Out2, "held off while the user decides");
                    ctl.ResumeAfterSwitchOff();
                    True(WaitFor(() => sim.Out2, 8000), "cancelled: regulating again");
                    device.RunAsync(d => d.Stop()).Wait();
                } finally {
                    device.Dispose();
                }
            });

            Run("Single PC410: a start that overtakes Stop doesn't switch the bed back on", () => {
                var settings = new AppSettings();
                settings.Bed.Mode = BedMode.SinglePc410;
                settings.Bed.Switching.CycleSeconds = 4;
                var prof = new ReflowProfile();
                prof.Steps.Add(new ProfileStep(1.5, 120, 20) { BedTarget = 110 });
                prof.Steps.Add(new ProfileStep(0.8, 160, 40) { BedTarget = 120 });
                prof.Steps.Add(new ProfileStep(1.0, 205, 20) { BedTarget = 130 });
                settings.SetSync(0, prof);
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, SimulatorSpeed = 10, PollIntervalMs = 200 }).Wait();
                    var sim = (SimulatedPc410)device.RunAsync(d => d.Client.Transport).Result;
                    device.ReadIn2 = device.ReadAlarm2 = true;
                    var ctl = new BedSwitchController(device, settings) { Enabled = true, KnownPattern = slot => slot == 0 ? prof : null };
                    Thread.Sleep(600);
                    device.RunAsync(d => d.Start(0)).Wait();
                    ctl.Stop(); // pressed before the controller took the Stop: the polls still show the run
                    ctl.Begin(BedPlan.From(settings.SyncFor(0), 25, 25));
                    Eq(null, ctl.Plan);
                    Thread.Sleep(1500);
                    False(sim.Out2, "bed stays off");
                    Eq("Stopped", ctl.Reason);
                    device.RunAsync(d => d.Stop()).Wait();
                    True(WaitFor(() => ctl.Reason == "Idle", 3000), "idle: " + ctl.Reason);
                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => sim.Out2, 8000), "the next run heats again");
                    device.RunAsync(d => d.Stop()).Wait();
                } finally {
                    device.Dispose();
                }
            });

            Run("Updates: release tags compare with the program's version", () => {
                Eq(new Version(2, 1, 1), UpdateChecker.ParseTag("v2.1.1"));
                Eq(new Version(2, 1, 1), UpdateChecker.ParseTag("2.1.1"));
                Eq(new Version(2, 2, 0), UpdateChecker.ParseTag("v2.2"));
                Eq(new Version(3, 0, 0), UpdateChecker.ParseTag("V3.0.0-beta"));
                Eq(null, UpdateChecker.ParseTag("latest"));
                // 2.1.1.0 (assembly version) and v2.1.1 (tag) are the same release.
                Eq(UpdateChecker.Normalize(new Version(2, 1, 1, 0)), UpdateChecker.ParseTag("v2.1.1"));
                True(UpdateChecker.ParseTag("v2.1.2") > UpdateChecker.Normalize(new Version(2, 1, 1, 0)), "newer");
                False(UpdateChecker.ParseTag("v2.1.0") > UpdateChecker.Normalize(new Version(2, 1, 1, 0)), "older");
                True(UpdateChecker.ParseTag("v2.10.0") > UpdateChecker.ParseTag("v2.9.0"), "compared as numbers, not text");
            });

            Run("Updates: GitHub's release reply is read, and only this repository's pages are opened", () => {
                string json = "{\"url\":\"https://api.github.com/repos/Sumutiu/ReflowLink/releases/1\",\"html_url\":\"https://github.com/Sumutiu/ReflowLink/releases/tag/v2.1.2\","
                    + "\"id\":1,\"author\":{\"login\":\"Sumutiu\",\"id\":7},\"tag_name\":\"v2.1.2\",\"name\":\"ReflowLink v2.1.2\",\"draft\":false,\"prerelease\":false,"
                    + "\"assets\":[{\"name\":\"ReflowLink.exe\",\"size\":123}],\"body\":\"## ReflowLink v2.1.2\\r\\n\\r\\n### Fixes\\r\\n- **Stop** works\\r\\n- `F12` too\"}";
                var info = UpdateChecker.Parse(json);
                Eq(new Version(2, 1, 2), info.Version);
                Eq("v2.1.2", info.Tag);
                Eq("https://github.com/Sumutiu/ReflowLink/releases/tag/v2.1.2", info.Url);
                Eq("Fixes\r\n• Stop works\r\n• F12 too", UpdateChecker.Summary(info.Notes).Replace(Environment.NewLine, "\r\n"));

                var foreign = UpdateChecker.Parse(json.Replace("https://github.com/Sumutiu/ReflowLink/releases/tag", "https://example.com/x"));
                Eq(UpdateChecker.ReleasesPage, foreign.Url);
                Eq(null, UpdateChecker.Parse(json.Replace("\"prerelease\":false", "\"prerelease\":true")));
                Eq(null, UpdateChecker.Parse(json.Replace("v2.1.2\",\"name", "nightly\",\"name")));
                string many = string.Join("\n", Enumerable.Range(1, 20).Select(i => "- item " + i));
                True(UpdateChecker.Summary(many).EndsWith("…"), "long notes are cut short");
            });

            Run("Single PC410: a missed step reading doesn't switch the bed off and on again", () => {
                var settings = new AppSettings();
                settings.Bed.Mode = BedMode.SinglePc410;
                settings.Bed.Switching.CycleSeconds = 4;
                var prof = new ReflowProfile();
                prof.Steps.Add(new ProfileStep(1.5, 120, 20) { BedTarget = 110 });
                prof.Steps.Add(new ProfileStep(0.8, 160, 40) { BedTarget = 120 });
                prof.Steps.Add(new ProfileStep(1.0, 205, 20) { BedTarget = 130 });
                settings.SetSync(0, prof);
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, SimulatorSpeed = 10, PollIntervalMs = 200, ResponseTimeoutMs = 100, Retries = 0 }).Wait();
                    var sim = (SimulatedPc410)device.RunAsync(d => d.Client.Transport).Result;
                    device.ReadIn2 = device.ReadAlarm2 = true;
                    var ctl = new BedSwitchController(device, settings) { Enabled = true, KnownPattern = slot => slot == 0 ? prof : null };
                    var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    ctl.Changed += () => reasons.Enqueue(ctl.Reason);
                    Thread.Sleep(600);
                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => ctl.Reason is "Heating" or "Regulating", 5000), "regulating: " + ctl.Reason);
                    while (reasons.TryDequeue(out _)) { }
                    // One or two polls without a step (a glitch on the cable).
                    sim.MissingParameters.Add("SE");
                    Thread.Sleep(350);
                    sim.MissingParameters.Remove("SE");
                    Thread.Sleep(600);
                    False(reasons.Contains("Step not reported"), "bridged: " + string.Join(", ", reasons.Distinct()));
                    // A step that stays missing does switch the bed off.
                    sim.MissingParameters.Add("SE");
                    True(WaitFor(() => ctl.Reason == "Step not reported", 9000), "lost: " + ctl.Reason);
                    True(WaitFor(() => !sim.Out2, 3000), "and the bed is off");
                    sim.MissingParameters.Remove("SE");
                    device.RunAsync(d => d.Stop()).Wait();
                } finally {
                    device.Dispose();
                }
            });

            Run("Polling: a missing optional reading costs one timeout, and a waiting command cuts the poll short", () => {
                var sim = new SimulatedPc410(1);
                sim.Open();
                using var c = new Pc410Client(sim, 1) { ResponseTimeoutMs = 100, Retries = 2 };
                var dev = new Pc410Device(c);
                dev.Start(0);
                sim.MissingParameters.Add("SP");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var t = dev.ReadTelemetry();
                Eq(null, t.Sp);
                True(sw.ElapsedMilliseconds < 250, "asked once, not three times: " + sw.ElapsedMilliseconds + " ms");
                True(t.Segment.HasValue && t.State == ProgramState.Running, "the rest is still read");
                True(Throws(() => c.Read("SP")) >= 290, "a normal read still retries");

                // A command waiting for the bus: the poll stops after PV.
                sim.MissingParameters.Remove("SP");
                sim.MissingParameters.UnionWith(["OS", "OP", "SE"]);
                var poll = System.Threading.Tasks.Task.Run(() => dev.ReadTelemetry());
                Thread.Sleep(30);
                sw.Restart();
                using (c.WaitingForBus()) {
                    lock (c.SyncRoot) { }
                }
                long waited = sw.ElapsedMilliseconds;
                var partial = poll.Result;
                True(waited < 180, "the command waited " + waited + " ms");
                True(partial.Partial && partial.Pv.HasValue, "partial poll with PV");
                dev.Stop();

                static long Throws(Action a) {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    try { a(); } catch (Pc410Exception) { }
                    return w.ElapsedMilliseconds;
                }
            });

            Run("Polling: a poll cut short shows the last complete readings", () => {
                var device = new DeviceService();
                try {
                    device.ConnectAsync(new ConnectionOptions { UseSimulator = true, PollIntervalMs = 200 }).Wait();
                    device.RunAsync(d => d.Start(0)).Wait();
                    True(WaitFor(() => device.Last?.State == ProgramState.Running && device.Last.Sp.HasValue, 3000), "running");
                    var seen = new System.Collections.Concurrent.ConcurrentQueue<Telemetry>();
                    device.TelemetryReceived += t => seen.Enqueue(t);
                    // Commands one after the other, so polls get cut short.
                    for (int i = 0; i < 15; i++) device.RunAsync(d => d.ReadState()).Wait();
                    Thread.Sleep(600);
                    True(seen.Count > 0, "polls arrived");
                    True(seen.All(t => t.State != ProgramState.Unknown && t.Sp.HasValue), "no gaps shown");
                    device.RunAsync(d => d.Stop()).Wait();
                } finally {
                    device.Dispose();
                }
            });

            Run("Backup & Restore: settings and profiles in one file, restored without overwriting", () => {
                string root = Path.Combine(Path.GetTempPath(), "reflowlink_backup_test_" + Guid.NewGuid().ToString("N"));
                string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
                Directory.CreateDirectory(source);
                try {
                    var a = new ReflowProfile { Name = "Leaded" };
                    a.Steps.Add(new ProfileStep(1, 150, 60));
                    a.Save(Path.Combine(source, "Leaded.rlprofile"));
                    var b = new ReflowProfile { Name = "Lead-free", Notes = "SAC305" };
                    b.Steps.Add(new ProfileStep(0.8, 217, 30));
                    b.Save(Path.Combine(source, "Lead-free.rlprofile"));
                    File.WriteAllText(Path.Combine(source, "notes.txt"), "not a profile");
                    File.WriteAllText(Path.Combine(source, "broken.rlprofile"), "<oops");

                    var s = new AppSettings {
                        StationId = StationCatalog.DefaultId,
                        MaxTemperature = 240,
                        LogFolder = @"C:\Users\someone\Logs",
                        LastProfilePath = @"C:\Users\someone\Documents\ReflowLink\Profiles\Leaded.rlprofile",
                        BlockedPatterns = [2]
                    };
                    s.Bed.Mode = BedMode.SinglePc410;
                    s.SetSync(1, b);
                    var backup = SetupBackup.Create(s, source);
                    Eq(2, backup.Profiles.Count);
                    Eq(1, backup.Unreadable.Count);
                    string file = Path.Combine(root, "setup.rlbackup");
                    backup.Save(file);
                    False(File.ReadAllText(file).Contains("someone"), "no paths of this PC in the file");

                    var loaded = SetupBackup.Load(file);
                    Eq(240.0, loaded.Settings.MaxTemperature);
                    Eq(BedMode.SinglePc410, loaded.Settings.Bed.Mode);
                    True(loaded.Settings.SyncFor(1)?.HasTargets == true, "pattern data kept");
                    Eq("SAC305", loaded.Profiles.Single(p => p.FileName == "Lead-free.rlprofile").Profile.Notes);

                    var r1 = loaded.RestoreProfiles(target);
                    Eq(2, r1.Added);
                    var r2 = loaded.RestoreProfiles(target);
                    Eq(2, r2.AlreadyThere);
                    var changed = new ReflowProfile { Name = "Leaded" };
                    changed.Steps.Add(new ProfileStep(2, 160, 10));
                    changed.Save(Path.Combine(target, "Leaded.rlprofile"));
                    var r3 = loaded.RestoreProfiles(target);
                    Eq(1, r3.Renamed);
                    Eq(160.0, ReflowProfile.Load(Path.Combine(target, "Leaded.rlprofile")).Steps[0].Target); // the user's own file is kept
                    True(File.Exists(Path.Combine(target, "Leaded (imported).rlprofile")), "the imported one gets a new name");

                    var here = new AppSettings { WindowX = 50, WindowWidth = 1500 };
                    var installed = loaded.SettingsForThisPc(here, target);
                    Eq(50, installed.WindowX);
                    Eq(1500, installed.WindowWidth);
                    Eq(Path.Combine(target, "Leaded.rlprofile"), installed.LastProfilePath);
                    Eq("", installed.LogFolder);
                    Throws<InvalidDataException>(() => SetupBackup.Load(Path.Combine(source, "broken.rlprofile")));
                } finally {
                    Directory.Delete(root, true);
                }
            });

            Run("Pattern comparison lists what differs", () => {
                var a = new ReflowProfile { Holdback = 5 };
                a.Steps.Add(new ProfileStep(1, 150, 30) { BedTarget = 140 });
                a.Steps.Add(new ProfileStep(1, 200, 10));
                var b = a.Clone();
                Eq(0, ProfileMath.Differences(a, b, true).Count);
                b.Steps[0].Target = 155;
                b.Steps[0].BedTarget = 120;
                b.Steps.RemoveAt(1);
                var d = ProfileMath.Differences(a, b, true);
                Eq(3, d.Count);
                Eq(2, ProfileMath.Differences(a, b, false).Count);
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

            Run("An old default temperature limit moves to 260 °C once", () => {
                var old = new AppSettings { StationId = "achi-ir6500", MaxTemperature = 230 };
                Eq(260.0, old.Sanitize().MaxTemperature);
                Eq(AppSettings.CurrentVersion, old.SettingsVersion);
                var custom = new AppSettings { StationId = "achi-ir6500", MaxTemperature = 240 };
                Eq(240.0, custom.Sanitize().MaxTemperature);
                var current = new AppSettings { StationId = "achi-ir6500", MaxTemperature = 230, SettingsVersion = AppSettings.CurrentVersion };
                Eq(230.0, current.Sanitize().MaxTemperature); // chosen after the change: kept
            });

            Run("Station catalog and settings", () => {
                Eq("achi-ir6500", StationCatalog.Find("unknown-id").Id);
                Eq(260.0, StationCatalog.Find("achi-ir6000").MaxTemperature);
                var s = new AppSettings();
                False(s.StationChosen, "no station before first run");
                s.SelectStation(StationCatalog.GenericId);
                Eq(260.0, s.MaxTemperature);
                s.GenericRampUnit = RampUnit.PerSecond;
                s.GenericDwellUnit = DwellUnit.Minutes;
                Eq(RampUnit.PerSecond, s.Units.Ramp);
                Eq(DwellUnit.Minutes, s.Units.Dwell);
                s.SelectStation("achi-ir-pro-sc");
                Eq(DwellUnit.Seconds, s.Units.Dwell);
                Eq(260.0, s.MaxTemperature);
            });

            Run("Targets above the controller limit are refused before writing", () => {
                using var c = NewSim(out _);
                var dev = new Pc410Device(c);
                var ok = new ReflowProfile();
                ok.Steps.Add(new ProfileStep(1, 150, 10));
                dev.UploadProfile(ok, 3, UnusedStepMode.Zeros, false, null, CancellationToken.None);
                var limits = dev.ReadSetpointLimits();
                Eq(260.0, limits.High);
                var hot = new ReflowProfile();
                hot.Steps.Add(new ProfileStep(1, 200, 10));
                hot.Steps.Add(new ProfileStep(1, 270, 10));
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(hot, 3, UnusedStepMode.Zeros, true, null, CancellationToken.None));
                False(ex.PatternChanged, "nothing written");
                True(ex.Issues[0].Contains("HS = 260"), ex.Issues[0]);
                Eq("150.0", c.Read("l1"));
            });

            Run("A target the controller rejects stops the write and clears the pattern", () => {
                using var c = NewSim(out var sim);
                sim.HideSetpointLimits = true;
                var dev = new Pc410Device(c);
                var hot = new ReflowProfile();
                hot.Steps.Add(new ProfileStep(1, 200, 10));
                hot.Steps.Add(new ProfileStep(1, 270, 10));
                hot.Steps.Add(new ProfileStep(1, 180, 10));
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(hot, 5, UnusedStepMode.Zeros, false, null, CancellationToken.None));
                True(ex.PatternChanged && ex.PatternCleared, "pattern cleared");
                True(ex.Issues.Count == 1 && ex.Issues[0].StartsWith("Step 2"), ex.Issues[0]);
                Eq("END", c.Read("r1"));
                Eq(0, dev.DownloadProfile(5, null, CancellationToken.None).Steps.Count);
            });

            Run("A target the controller silently limits is caught even without verify", () => {
                using var c = NewSim(out var sim);
                sim.HideSetpointLimits = true;
                sim.ClampTargets = true;
                var dev = new Pc410Device(c);
                var hot = new ReflowProfile();
                hot.Steps.Add(new ProfileStep(1, 275, 10));
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(hot, 6, UnusedStepMode.Zeros, false, null, CancellationToken.None));
                True(ex.PatternCleared, "pattern cleared");
                True(ex.Issues[0].Contains("changed to 260"), ex.Issues[0]);
            });

            Run("A communication failure mid-write blocks the pattern", () => {
                var sim = new SimulatedPc410(1);
                var flaky = new FlakyTransport(sim);
                flaky.Open();
                using var c = new Pc410Client(flaky, 1) { ResponseTimeoutMs = 60, Retries = 0 };
                var dev = new Pc410Device(c);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 150, 10));
                p.Steps.Add(new ProfileStep(1, 200, 10));
                flaky.SilentAfterWrites = 12; // limits, pattern, holdback and part of the steps get through
                var ex = Catch<ProfileRejectedException>(() => dev.UploadProfile(p, 4, UnusedStepMode.Zeros, false, null, CancellationToken.None));
                True(ex.Interrupted && ex.PatternChanged, "interrupted while writing");
                False(ex.PatternCleared, "controller silent, so the pattern couldn't be cleared");
                True(ex.Issues[0].StartsWith("Communication failed"), ex.Issues[0]);
            });

            Run("A glitch on HS never switches the limit check off", () => {
                var sim = new SimulatedPc410(1);
                var flaky = new FlakyTransport(sim);
                flaky.Open();
                using var c = new Pc410Client(flaky, 1) { ResponseTimeoutMs = 60, Retries = 0 };
                var dev = new Pc410Device(c);
                Eq(260.0, dev.ReadSetpointLimits().High);
                flaky.SilentAfterWrites = 0;
                Throws<Pc410TimeoutException>(() => dev.ReadSetpointLimits());
                flaky.SilentAfterWrites = int.MaxValue;
                Eq(260.0, dev.ReadSetpointLimits().High);
            });

            Run("Start check refuses a pattern outside the limits", () => {
                var sim = new SimulatedPc410(1, 1, null, 260);
                sim.Open();
                using var c = new Pc410Client(sim, 1) { ResponseTimeoutMs = 150, Retries = 1 };
                var dev = new Pc410Device(c);
                var p = new ReflowProfile();
                p.Steps.Add(new ProfileStep(1, 245, 10));
                dev.UploadProfile(p, 2, UnusedStepMode.Zeros, true, null, CancellationToken.None);
                Eq(1, dev.CheckPattern(2, 250, null, CancellationToken.None).Steps.Count);
                var ex = Catch<ProfileRejectedException>(() => dev.CheckPattern(2, 230, null, CancellationToken.None));
                True(ex.Issues[0].Contains("ReflowLink's limit"), ex.Issues[0]);
                c.Write("HS", "240");
                ex = Catch<ProfileRejectedException>(() => dev.CheckPattern(2, 250, null, CancellationToken.None));
                True(ex.Issues[0].Contains("HS = 240"), ex.Issues[0]);
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

        /// <summary>Passes traffic through until a number of writes, then stays silent (unplugged cable).</summary>
        private sealed class FlakyTransport(ITransport inner) : ITransport {
            private int _writes;
            public int SilentAfterWrites { get; set; } = int.MaxValue;
            public string Name => "flaky";
            public bool IsOpen => inner.IsOpen;
            public void Open() => inner.Open();
            public void Close() => inner.Close();
            public void DiscardInput() => inner.DiscardInput();
            public void Write(byte[] data) {
                if (_writes++ >= SilentAfterWrites) return;
                inner.Write(data);
            }
            public int ReadByte(int timeoutMs) => inner.ReadByte(timeoutMs);
            public void Dispose() => inner.Dispose();
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

        private static void Eq(double expected, double actual, double tolerance) {
            if (Math.Abs(expected - actual) > tolerance)
                throw new Exception($"expected <{expected}> ± {tolerance} but got <{actual}>");
        }

        /// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it elsewhere).</summary>
        private sealed class SyncProgress(Action<OperationProgress> report) : IProgress<OperationProgress> {
            public void Report(OperationProgress value) => report(value);
        }

        /// <summary>A serial port that fails like an unplugged USB adapter.</summary>
        private sealed class BrokenTransport : ITransport {
            public int Writes;
            public string Name => "COM9";
            public bool IsOpen => true;
            public void Open() { }
            public void Close() { }
            public void DiscardInput() { }
            public void Write(byte[] data) {
                Writes++;
                throw new IOException("The device is not connected.");
            }
            public int ReadByte(int timeoutMs) => -1;
            public void Dispose() { }
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs) {
                if (cond()) return true;
                Thread.Sleep(50);
            }
            return cond();
        }

        private static void True(bool cond, string what) {
            if (!cond) throw new Exception("assertion failed: " + what);
        }

        private static TEx Catch<TEx>(Action a) where TEx : Exception {
            try { a(); } catch (TEx ex) { return ex; } catch (Exception ex) { throw new Exception($"expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new Exception($"expected {typeof(TEx).Name}, nothing thrown");
        }

        private static void Throws<TEx>(Action a) where TEx : Exception {
            try { a(); } catch (TEx) { return; } catch (Exception ex) { throw new Exception($"expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new Exception($"expected {typeof(TEx).Name}, nothing thrown");
        }

        private static string Ascii(byte[] b) => System.Text.Encoding.ASCII.GetString(b);
        private static string Hex(byte[] b) => Pc410Protocol.ToHex(b, 0, b.Length);
    }
}
