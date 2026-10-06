// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Globalization;
using System.Text;

namespace ReflowLink.Protocol {
    /// <summary>
    /// Low-level frame encoding/decoding for the ALTEC PC410 / AL808 / PC900 ASCII protocol.
    ///
    ///   Read  (poll):   EOT A A B B C1 C2 ENQ              -> STX C1 C2 DATA ETX BCC   (silence on error)
    ///   Write (select): EOT A A B B STX C1 C2 DATA ETX BCC -> ACK | NAK                (silence on error)
    ///
    /// The address (00..99) is sent as its two decimal digits, each repeated twice.
    /// BCC = XOR of every byte after STX, up to and including ETX.
    /// UART format is 7E1 (7 data bits, even parity, 1 stop bit).
    /// </summary>
    public static class Pc410Protocol {
        public const byte STX = 0x02;
        public const byte ETX = 0x03;
        public const byte EOT = 0x04;
        public const byte ENQ = 0x05;
        public const byte ACK = 0x06;
        public const byte NAK = 0x15;

        public const int MinAddress = 0;
        public const int MaxAddress = 99;
        public const int SegmentCount = 8;
        public const int PatternMin = 0;
        public const int PatternMax = 9;

        /// <summary>Special ramp-rate value that terminates the program at that segment.</summary>
        public const string EndValue = "END";

        public static readonly int[] StandardBaudRates = [300, 600, 1200, 2400, 4800, 9600, 19200];

        public static byte[] EncodeAddress(int address) {
            if (address is < MinAddress or > MaxAddress)
                throw new ArgumentOutOfRangeException(nameof(address), "Address must be 0..99.");
            string d = address.ToString("00", CultureInfo.InvariantCulture);
            return [(byte)d[0], (byte)d[0], (byte)d[1], (byte)d[1]];
        }

        public static byte Bcc(byte[] data, int offset, int count) {
            byte b = 0;
            for (int i = offset; i < offset + count; i++) b ^= data[i];
            return b;
        }

        public static void ValidateMnemonic(string mnemonic) {
            if (mnemonic == null || mnemonic.Length != 2)
                throw new ArgumentException("Mnemonic must be exactly 2 characters.", nameof(mnemonic));
            foreach (char c in mnemonic)
                if (c is < (char)0x21 or > (char)0x7E)
                    throw new ArgumentException("Mnemonic must be printable ASCII.", nameof(mnemonic));
        }

        public static void ValidateData(string data) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            foreach (char c in data)
                if (c is < (char)0x20 or > (char)0x7E)
                    throw new ArgumentException("Data must be printable ASCII.", nameof(data));
        }

        public static byte[] BuildReadFrame(int address, string mnemonic) {
            ValidateMnemonic(mnemonic);
            var a = EncodeAddress(address);
            return [EOT, a[0], a[1], a[2], a[3], (byte)mnemonic[0], (byte)mnemonic[1], ENQ];
        }

        public static byte[] BuildWriteFrame(int address, string mnemonic, string data) {
            ValidateMnemonic(mnemonic);
            ValidateData(data);
            var a = EncodeAddress(address);
            var f = new byte[5 + 1 + 2 + data.Length + 1 + 1];
            int i = 0;
            f[i++] = EOT; f[i++] = a[0]; f[i++] = a[1]; f[i++] = a[2]; f[i++] = a[3];
            f[i++] = STX;
            int payloadStart = i;
            f[i++] = (byte)mnemonic[0]; f[i++] = (byte)mnemonic[1];
            foreach (char c in data) f[i++] = (byte)c;
            f[i++] = ETX;
            f[i] = Bcc(f, payloadStart, i - payloadStart);
            return f;
        }

        /// <summary>Builds the reply a controller sends to a read request (used by the simulator and tests).</summary>
        public static byte[] BuildReadResponse(string mnemonic, string data) {
            ValidateMnemonic(mnemonic);
            ValidateData(data);
            var f = new byte[1 + 2 + data.Length + 1 + 1];
            int i = 0;
            f[i++] = STX;
            f[i++] = (byte)mnemonic[0]; f[i++] = (byte)mnemonic[1];
            foreach (char c in data) f[i++] = (byte)c;
            f[i++] = ETX;
            f[i] = Bcc(f, 1, i - 1);
            return f;
        }

        /// <summary>
        /// Returns the length of a complete read response at the start of <paramref name="buf"/>,
        /// or -1 if more bytes are needed. Leading garbage before STX is reported via <paramref name="skip"/>.
        /// </summary>
        public static int FindCompleteReadResponse(byte[] buf, int count, out int skip) {
            skip = 0;
            while (skip < count && buf[skip] != STX) skip++;
            for (int i = skip + 1; i < count; i++) {
                if (buf[i] == ETX)
                    return i + 1 < count ? (i + 2 - skip) : -1;
            }
            return -1;
        }

        /// <summary>Validates a read response and returns its DATA field.</summary>
        public static string ParseReadResponse(byte[] resp, int offset, int length, string expectedMnemonic) {
            if (resp == null || length == 0) throw new Pc410TimeoutException("No response from controller.");
            if (resp[offset] != STX) throw new Pc410FrameException("Response does not start with STX.");
            if (length < 5) throw new Pc410FrameException("Response is too short.");
            int etxIndex = offset + length - 2;
            if (resp[etxIndex] != ETX) throw new Pc410FrameException("Response is missing ETX.");
            byte calc = Bcc(resp, offset + 1, length - 2);
            byte got = resp[offset + length - 1];
            if (calc != got)
                throw new Pc410ChecksumException($"Checksum mismatch (calculated 0x{calc:X2}, received 0x{got:X2}).");
            string mn = Encoding.ASCII.GetString(resp, offset + 1, 2);
            if (expectedMnemonic != null && mn != expectedMnemonic)
                throw new Pc410FrameException($"Unexpected parameter in reply: expected '{expectedMnemonic}', got '{mn}'.");
            return Encoding.ASCII.GetString(resp, offset + 3, length - 5);
        }

        // ----- Value formatting -----

        public static string FormatNumber(double value, int maxDecimals = 2) {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            double r = Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero);
            if (Math.Abs(r - Math.Round(r)) < 1e-9) return ((long)Math.Round(r)).ToString(CultureInfo.InvariantCulture);
            string s = r.ToString("0." + new string('#', Math.Max(1, maxDecimals)), CultureInfo.InvariantCulture);
            return s;
        }

        public static string FormatInteger(double value) =>
            ((long)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

        public static string FormatHexWord(int value) => ">" + (value & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);

        public static bool IsEnd(string data) => data != null && data.Trim().Equals(EndValue, StringComparison.OrdinalIgnoreCase);

        public static bool TryParseNumber(string data, out double value) {
            value = 0;
            if (data == null) return false;
            string t = data.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith(">", StringComparison.Ordinal)) {
                if (TryParseHexWord(t, out int w)) { value = w; return true; }
                return false;
            }
            if (t.EndsWith(".", StringComparison.Ordinal)) t += "0";
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseHexWord(string data, out int value) {
            value = 0;
            if (data == null) return false;
            string t = data.Trim();
            if (t.StartsWith(">", StringComparison.Ordinal)) t = t.Substring(1);
            return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        // ----- Diagnostics -----

        public static string Describe(byte[] data, int offset, int count) {
            var sb = new StringBuilder();
            for (int i = offset; i < offset + count; i++) {
                byte b = data[i];
                switch (b) {
                    case STX: sb.Append("<STX>"); break;
                    case ETX: sb.Append("<ETX>"); break;
                    case EOT: sb.Append("<EOT>"); break;
                    case ENQ: sb.Append("<ENQ>"); break;
                    case ACK: sb.Append("<ACK>"); break;
                    case NAK: sb.Append("<NAK>"); break;
                    default:
                        // The byte right after ETX is the checksum: always show it as hex.
                        if (i > offset && data[i - 1] == ETX) sb.Append('[').Append(b.ToString("X2")).Append(']');
                        else if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
                        else sb.Append('[').Append(b.ToString("X2")).Append(']');
                        break;
                }
            }
            return sb.ToString();
        }

        public static string ToHex(byte[] data, int offset, int count) {
            var sb = new StringBuilder(count * 3);
            for (int i = offset; i < offset + count; i++) {
                if (i > offset) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }

    public class Pc410Exception : Exception {
        public Pc410Exception(string message) : base(message) { }
        public Pc410Exception(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class Pc410TimeoutException(string message) : Pc410Exception(message) {
    }

    public sealed class Pc410NakException(string message) : Pc410Exception(message) {
    }

    public sealed class Pc410FrameException(string message) : Pc410Exception(message) {
    }

    public sealed class Pc410ChecksumException(string message) : Pc410Exception(message) {
    }
}
