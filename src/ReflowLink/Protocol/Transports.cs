// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.IO.Ports;

namespace ReflowLink.Protocol {
    /// <summary>Byte-level link to a controller (real serial port or the built-in simulator).</summary>
    public interface ITransport : IDisposable {
        string Name { get; }
        bool IsOpen { get; }
        void Open();
        void Close();
        void DiscardInput();
        void Write(byte[] data);
        /// <summary>Returns the next byte, or -1 if nothing arrived within <paramref name="timeoutMs"/>.</summary>
        int ReadByte(int timeoutMs);
    }

    public sealed class SerialSettings {
        public string PortName = "COM1";
        public int BaudRate = 9600;
        public int DataBits = 7;
        public Parity Parity = Parity.Even;
        public StopBits StopBits = StopBits.One;
        public bool DtrEnable = true;
        public bool RtsEnable = true;
    }

    public sealed class SerialTransport : ITransport {
        private readonly SerialPort _port;

        public SerialTransport(SerialSettings s) {
            _port = new SerialPort(s.PortName, s.BaudRate, s.Parity, s.DataBits, s.StopBits) {
                Handshake = Handshake.None,
                DtrEnable = s.DtrEnable,
                RtsEnable = s.RtsEnable,
                ReadTimeout = 500,
                WriteTimeout = 1000,
                ReadBufferSize = 4096,
                WriteBufferSize = 2048,
            };
            Name = $"{s.PortName} · {s.BaudRate} {s.DataBits}{ParityChar(s.Parity)}{(s.StopBits == StopBits.Two ? 2 : 1)}";
        }

        private static char ParityChar(Parity p) {
            return p switch {
                Parity.Even => 'E',
                Parity.Odd => 'O',
                Parity.Mark => 'M',
                Parity.Space => 'S',
                _ => 'N',
            };
        }

        public string Name { get; }
        public bool IsOpen => _port.IsOpen;

        public void Open() {
            _port.Open();
            // Known .NET Framework issue: if a USB adapter is unplugged, the SerialStream finalizer
            // can throw on the finalizer thread and kill the process. We close the stream ourselves instead.
            try { _stream = _port.BaseStream; GC.SuppressFinalize(_stream); } catch { }
            DiscardInput();
        }

        private System.IO.Stream _stream;

        public void Close() {
            try { if (_port.IsOpen) _port.Close(); } catch { /* port may have vanished (USB unplugged) */ }
            var s = _stream;
            _stream = null;
            if (s != null) { try { s.Dispose(); } catch { } }
        }

        public void DiscardInput() {
            try { if (_port.IsOpen) _port.DiscardInBuffer(); } catch { }
        }

        public void Write(byte[] data) => _port.Write(data, 0, data.Length);

        public int ReadByte(int timeoutMs) {
            try {
                if (_port.ReadTimeout != timeoutMs) _port.ReadTimeout = Math.Max(1, timeoutMs);
                return _port.ReadByte();
            } catch (TimeoutException) {
                return -1;
            }
        }

        public void Dispose() {
            Close();
            try { _port.Dispose(); } catch { }
        }

        public static string[] GetPortNames() {
            try {
                var names = SerialPort.GetPortNames();
                Array.Sort(names, ComparePortNames);
                return names;
            } catch { return []; }
        }

        private static int ComparePortNames(string a, string b) {
            int na = TrailingNumber(a), nb = TrailingNumber(b);
            if (na >= 0 && nb >= 0 && string.Equals(StripNumber(a), StripNumber(b), StringComparison.OrdinalIgnoreCase))
                return na.CompareTo(nb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripNumber(string s) {
            int i = s.Length;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;
            return s.Substring(0, i);
        }

        private static int TrailingNumber(string s) {
            string p = StripNumber(s);
            return int.TryParse(s.Substring(p.Length), out int n) ? n : -1;
        }
    }
}
