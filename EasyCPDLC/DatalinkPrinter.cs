using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Globalization;

namespace EasyCPDLC
{
    internal enum DatalinkPrinterMode
    {
        Windows,
        RawEscPos,
        MockFile
    }

    internal enum DatalinkPrinterProfile
    {
        CitizenCtS4000_112Mm,
        GenericEscPos80Mm
    }

    internal enum DatalinkCutMode
    {
        Off,
        Partial,
        Full
    }

    internal enum DatalinkMessageCategory
    {
        Other,
        PdcDcl,
        Cpdlc,
        TelexAoc,
        Atis
    }

    internal sealed class DatalinkPrinterSettings
    {
        public DatalinkPrinterMode Mode { get; init; } = DatalinkPrinterMode.RawEscPos;
        public DatalinkPrinterProfile Profile { get; init; } = DatalinkPrinterProfile.CitizenCtS4000_112Mm;
        public string PrinterName { get; init; } = string.Empty;
        public int Columns { get; init; } = 69;
        public bool AutoPrintPdcDcl { get; init; }
        public bool AutoPrintCpdlc { get; init; }
        public bool AutoPrintTelexAoc { get; init; }
        public bool AutoPrintAtis { get; init; }
        public DatalinkCutMode CutMode { get; init; } = DatalinkCutMode.Partial;
        public int FeedLines { get; init; } = 3;
        public string MockOutputDirectory { get; init; } = string.Empty;
    }

    internal sealed class DatalinkPrintJob
    {
        public string Sender { get; init; } = "UNKNOWN";
        public string MessageType { get; init; } = "TELEX";
        public string Message { get; init; } = string.Empty;
        public string AircraftCallsign { get; init; } = string.Empty;
        public string TransportSource { get; init; } = string.Empty;
        public string StableMessageId { get; init; } = string.Empty;
        public DateTime ReceivedLocalTime { get; init; } = DateTime.Now;
        public DatalinkMessageCategory Category { get; init; } = DatalinkMessageCategory.Other;

        public static DatalinkPrintJob FromMessage(CPDLCMessage message, string aircraftCallsign = null)
        {
            string sender = string.IsNullOrWhiteSpace(message?.recipient) ? "UNKNOWN" : message.recipient.Trim();
            string messageType = string.IsNullOrWhiteSpace(message?.type) ? "TELEX" : message.type.Trim();
            string body = DatalinkPrinter.NormalizeLineEndings(message?.message ?? string.Empty).TrimEnd();
            string cleanCallsign = !string.IsNullOrWhiteSpace(message?.aircraftCallsign)
                ? message.aircraftCallsign.Trim()
                : (aircraftCallsign ?? string.Empty).Trim();
            return new DatalinkPrintJob
            {
                Sender = sender,
                MessageType = messageType,
                Message = body,
                AircraftCallsign = cleanCallsign,
                TransportSource = (message?.transport ?? string.Empty).Trim(),
                StableMessageId = DatalinkPrinter.DeriveStableMessageId(message, cleanCallsign),
                ReceivedLocalTime = DateTime.Now,
                Category = DatalinkPrinter.ClassifyMessage(message)
            };
        }
    }

    internal sealed class DatalinkPrinterStatus
    {
        public bool IsReady { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    internal sealed class DatalinkPrintResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string OutputPath { get; init; } = string.Empty;
    }

    internal static class DatalinkPrinter
    {
        private static readonly Encoding PrinterEncoding;
        private const uint PrinterStatusPaused = 0x00000001;
        private const uint PrinterStatusError = 0x00000002;
        private const uint PrinterStatusPaperOut = 0x00000010;
        private const uint PrinterStatusOffline = 0x00000080;
        private const uint PrinterStatusNotAvailable = 0x00001000;
        private const uint PrinterStatusUserIntervention = 0x00100000;
        private const uint PrinterStatusDoorOpen = 0x00400000;

        private static readonly Regex LoadsheetWeightPattern = new(
            @"\b(?:ZFW|TOW|LAW|LDW|MZFW|MTOW|MLDW|MACZFW|MACTOW|MACLAW|MACLDW)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClearancePattern = new(
            @"\b(?:PDC|DCL|PREDEP|PRE-DEPARTURE|CLEARANCE|CLRD|CLD)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static DatalinkPrinter()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            PrinterEncoding = Encoding.GetEncoding(
                437,
                new EncoderReplacementFallback("?"),
                new DecoderReplacementFallback("?"));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
            [MarshalAs(UnmanagedType.LPWStr)] public string OutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PrinterInfo2
        {
            public IntPtr ServerName;
            public IntPtr PrinterName;
            public IntPtr ShareName;
            public IntPtr PortName;
            public IntPtr DriverName;
            public IntPtr Comment;
            public IntPtr Location;
            public IntPtr DevMode;
            public IntPtr SeparatorFile;
            public IntPtr PrintProcessor;
            public IntPtr DataType;
            public IntPtr Parameters;
            public IntPtr SecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint JobCount;
            public uint AveragePagesPerMinute;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr printerHandle);

        [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetPrinter(IntPtr printerHandle, uint level, IntPtr buffer, uint bufferSize, out uint needed);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint StartDocPrinter(IntPtr printerHandle, uint level, [In] ref DocInfo1 documentInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr printerHandle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr printerHandle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr printerHandle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr printerHandle, IntPtr buffer, uint byteCount, out uint bytesWritten);

        public static IReadOnlyList<string> GetInstalledPrinterNames()
        {
            try
            {
                return PrinterSettings.InstalledPrinters.Cast<string>()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static string GetDefaultPrinterName()
        {
            try
            {
                PrinterSettings settings = new();
                return settings.IsValid ? settings.PrinterName ?? string.Empty : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetProfileDisplayName(DatalinkPrinterProfile profile)
        {
            return profile switch
            {
                DatalinkPrinterProfile.GenericEscPos80Mm => "GENERIC ESC/POS 80MM",
                _ => "GENERIC 4 INCH (104MM PRINTABLE)"
            };
        }

        public static string GetProfileShortName(DatalinkPrinterProfile profile)
        {
            return profile switch
            {
                DatalinkPrinterProfile.GenericEscPos80Mm => "GENERIC 80MM",
                _ => "GENERIC 4 INCH"
            };
        }

        public static int GetNormalColumns(DatalinkPrinterProfile profile)
        {
            return profile == DatalinkPrinterProfile.GenericEscPos80Mm ? 48 : 69;
        }

        public static int GetCondensedColumns(DatalinkPrinterProfile profile)
        {
            return profile == DatalinkPrinterProfile.GenericEscPos80Mm ? 64 : 92;
        }

        internal static int NormalizeProfileColumns(DatalinkPrinterProfile profile, int requestedColumns)
        {
            int normal = GetNormalColumns(profile);
            int condensed = GetCondensedColumns(profile);
            return requestedColumns == condensed ? condensed : normal;
        }

        internal static bool UsesCondensedFont(DatalinkPrinterProfile profile, int requestedColumns)
        {
            return NormalizeProfileColumns(profile, requestedColumns) == GetCondensedColumns(profile);
        }

        public static bool IsPrintableMessage(CPDLCMessage message)
        {
            return message != null &&
                !message.outbound &&
                !string.IsNullOrWhiteSpace(message.message) &&
                ClassifyMessage(message) != DatalinkMessageCategory.Other;
        }

        public static DatalinkMessageCategory ClassifyMessage(CPDLCMessage message)
        {
            if (message == null || message.outbound || string.IsNullOrWhiteSpace(message.message))
            {
                return DatalinkMessageCategory.Other;
            }

            string type = (message.type ?? string.Empty).Trim().ToUpperInvariant();
            string text = message.message.Trim();
            string combined = type + " " + text;

            if (ClearancePattern.IsMatch(combined))
            {
                return DatalinkMessageCategory.PdcDcl;
            }

            if (string.Equals(type, "ATIS", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains(" ATIS ", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("_ATIS", StringComparison.OrdinalIgnoreCase))
            {
                return DatalinkMessageCategory.Atis;
            }

            if (string.Equals(type, "CPDLC", StringComparison.OrdinalIgnoreCase))
            {
                return DatalinkMessageCategory.Cpdlc;
            }

            // LOADSHEET belongs here too. A loadsheet the VA sends arrives as a TELEX
            // and classified fine, but the ones this app generates are written with
            // type "LOADSHEET" - which fell through to Other, making them unprintable
            // and invisible to IsELoadControlLoadsheet, which tests printability first.
            if (string.Equals(type, "TELEX", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "INFO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "AOC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "LOADSHEET", StringComparison.OrdinalIgnoreCase))
            {
                return DatalinkMessageCategory.TelexAoc;
            }

            return DatalinkMessageCategory.Other;
        }

        public static bool IsELoadControlLoadsheet(CPDLCMessage message)
        {
            if (!IsPrintableMessage(message))
            {
                return false;
            }

            string text = message.message;
            if (text.Contains("ELOADCONTROL", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("LOADSHEET", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("LOAD PLANNING", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("SANDBOX ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return LoadsheetWeightPattern.Matches(text).Count >= 2;
        }

        public static bool ShouldAutoPrint(DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            if (job == null || settings == null)
            {
                return false;
            }

            return job.Category switch
            {
                DatalinkMessageCategory.PdcDcl => settings.AutoPrintPdcDcl,
                DatalinkMessageCategory.Cpdlc => settings.AutoPrintCpdlc,
                DatalinkMessageCategory.TelexAoc => settings.AutoPrintTelexAoc,
                DatalinkMessageCategory.Atis => settings.AutoPrintAtis,
                _ => false
            };
        }

        public static DatalinkPrintJob CreateTestJob()
        {
            DatalinkPrintJob job = new()
            {
                Sender = "DISPATCH",
                MessageType = "LOADSHEET",
                AircraftCallsign = "B-18662",
                TransportSource = "HOPPIE",
                Category = DatalinkMessageCategory.TelexAoc,
                ReceivedLocalTime = new DateTime(2026, 7, 20, 18, 42, 0, DateTimeKind.Utc).ToLocalTime(),
                Message =
                    "FROM: DISPATCH\n" +
                    "TO: B-18662\n" +
                    "FLT: CI7752\n" +
                    "SFO-SNA\n" +
                    "PAX 155\n" +
                    "ZFW 129393\n" +
                    "TOW 143115\n" +
                    "LDW 136053\n" +
                    "BLOCK FUEL 14500\n" +
                    "TRIP FUEL 7062\n" +
                    "MACZFW 25.9\n" +
                    "MACTOW 27.0\n" +
                    "END OF LOADSHEET"
            };
            return new DatalinkPrintJob
            {
                Sender = job.Sender,
                MessageType = job.MessageType,
                AircraftCallsign = job.AircraftCallsign,
                TransportSource = job.TransportSource,
                Category = job.Category,
                ReceivedLocalTime = job.ReceivedLocalTime,
                Message = job.Message,
                StableMessageId = DeriveStableMessageId(job.MessageType, job.Sender, job.AircraftCallsign, job.Message)
            };
        }

        public static DatalinkPrinterStatus GetPrinterStatus(string configuredPrinterName)
        {
            string printerName = ResolvePrinterName(configuredPrinterName);
            if (string.IsNullOrWhiteSpace(printerName))
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "NO PRINTER" };
            }

            if (!OpenPrinter(printerName, out IntPtr handle, IntPtr.Zero) || handle == IntPtr.Zero)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "UNAVAILABLE" };
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                _ = GetPrinter(handle, 2, IntPtr.Zero, 0, out uint needed);
                if (needed == 0)
                {
                    return new DatalinkPrinterStatus { IsReady = true, Message = "READY" };
                }

                buffer = Marshal.AllocHGlobal((int)needed);
                if (!GetPrinter(handle, 2, buffer, needed, out _))
                {
                    return new DatalinkPrinterStatus { IsReady = false, Message = "STATUS ERROR" };
                }

                PrinterInfo2 info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                return StatusFromFlags(info.Status, info.JobCount);
            }
            catch
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "STATUS ERROR" };
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                ClosePrinter(handle);
            }
        }

        public static DatalinkPrintResult Print(DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.Message))
            {
                return Failed("NO PRINTABLE ACARS MESSAGE IS AVAILABLE.");
            }

            settings ??= new DatalinkPrinterSettings();
            if (settings.Mode == DatalinkPrinterMode.MockFile)
            {
                return PrintMockFile(job, settings);
            }

            string printerName = ResolvePrinterName(settings.PrinterName);
            if (string.IsNullOrWhiteSpace(printerName))
            {
                if (!string.IsNullOrWhiteSpace(settings.PrinterName))
                {
                    return Failed("THE SELECTED PRINTER IS NOT INSTALLED: " + settings.PrinterName.Trim());
                }
                return Failed("NO WINDOWS PRINTER IS INSTALLED OR SELECTED.");
            }

            DatalinkPrinterStatus status = GetPrinterStatus(printerName);
            if (!status.IsReady)
            {
                return Failed("PRINTER " + status.Message + ": " + printerName);
            }

            return settings.Mode switch
            {
                DatalinkPrinterMode.RawEscPos => PrintRawEscPos(printerName, job, settings),
                _ => PrintWindows(printerName, job, settings)
            };
        }

        public static string FormatReceiptText(DatalinkPrintJob job, int requestedColumns)
        {
            int columns = NormalizeReceiptColumns(requestedColumns);
            string type = "TYPE " + SafeToken(job?.MessageType, "TELEX");
            string timestamp = "UTC " + (job?.ReceivedLocalTime ?? DateTime.Now)
                .ToUniversalTime()
                .ToString("ddMMMyy HHmm'Z'", CultureInfo.InvariantCulture)
                .ToUpperInvariant();
            List<string> lines = new()
            {
                Center("ACARS", columns),
                new string('-', columns)
            };
            lines.AddRange(WrapLines(JoinColumns(type, timestamp, columns), columns));

            if (!string.IsNullOrWhiteSpace(job?.TransportSource))
            {
                lines.AddRange(WrapLines("SOURCE " + SafeToken(job.TransportSource, string.Empty), columns));
            }

            string aircraft = string.IsNullOrWhiteSpace(job?.AircraftCallsign)
                ? string.Empty
                : "A/C " + SafeToken(job.AircraftCallsign, string.Empty);
            string sender = string.IsNullOrWhiteSpace(job?.Sender)
                ? string.Empty
                : "FROM " + SafeToken(job.Sender, "UNKNOWN");
            if (aircraft.Length > 0 || sender.Length > 0)
            {
                lines.AddRange(WrapLines(JoinColumns(aircraft, sender, columns), columns));
            }
            lines.Add(new string('-', columns));
            lines.AddRange(WrapLines(job?.Message ?? string.Empty, columns));
            lines.Add(new string('-', columns));
            return string.Join("\n", lines);
        }

        private static DatalinkPrintResult PrintMockFile(DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            try
            {
                string directory = string.IsNullOrWhiteSpace(settings.MockOutputDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyCPDLCModernized", "PrinterMock")
                    : Path.GetFullPath(settings.MockOutputDirectory);
                Directory.CreateDirectory(directory);

                int columns = NormalizeProfileColumns(settings.Profile, settings.Columns);
                byte[] payload = BuildEscPosPayload(job, settings);
                string id = SanitizeFileToken(job.StableMessageId);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                }
                string basePath = Path.Combine(directory, "ACARS-" + id);
                string binaryPath = UniquePath(basePath, ".bin");
                File.WriteAllBytes(binaryPath, payload);
                File.WriteAllText(Path.ChangeExtension(binaryPath, ".hex.txt"), ToHexDump(payload), Encoding.ASCII);
                File.WriteAllText(Path.ChangeExtension(binaryPath, ".preview.txt"), FormatReceiptText(job, columns), Encoding.UTF8);
                return new DatalinkPrintResult
                {
                    Success = true,
                    Message = "MOCK ESC/POS JOB SAVED: " + binaryPath,
                    OutputPath = binaryPath
                };
            }
            catch (Exception ex)
            {
                return Failed("MOCK PRINT FAILED: " + ex.Message);
            }
        }

        private static DatalinkPrintResult PrintWindows(string printerName, DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            int columns = NormalizeProfileColumns(settings.Profile, settings.Columns);
            List<string> lines = FormatReceiptText(job, columns).Split('\n').ToList();
            int nextLine = 0;

            try
            {
                using PrintDocument document = new();
                document.DocumentName = "EasyCPDLC ACARS " + job.MessageType;
                document.PrintController = new StandardPrintController();
                document.PrinterSettings.PrinterName = printerName;
                // Keep a narrow physical margin for receipt printers. Font sizing below
                // still adapts to the actual MarginBounds reported by the selected driver.
                document.DefaultPageSettings.Margins = new Margins(10, 10, 10, 10);

                if (!document.PrinterSettings.IsValid)
                {
                    return Failed("THE SELECTED WINDOWS PRINTER IS INVALID: " + printerName);
                }

                document.PrintPage += (_, e) =>
                {
                    using Font bodyFont = CreateWindowsBodyFont(e.Graphics, e.MarginBounds.Width, columns);
                    using Font headingFont = new(
                        "Consolas",
                        Math.Max(bodyFont.Size + 2f, bodyFont.Size * 1.45f),
                        FontStyle.Bold,
                        GraphicsUnit.Point);
                    float currentY = e.MarginBounds.Top;

                    while (nextLine < lines.Count)
                    {
                        string line = nextLine == 0 ? lines[nextLine].Trim() : lines[nextLine].TrimEnd();
                        Font font = nextLine == 0 ? headingFont : bodyFont;
                        float lineHeight = font.GetHeight(e.Graphics) + 1f;
                        if (currentY + lineHeight > e.MarginBounds.Bottom && currentY > e.MarginBounds.Top)
                        {
                            break;
                        }

                        RectangleF lineBounds = new(
                            e.MarginBounds.Left,
                            currentY,
                            e.MarginBounds.Width,
                            lineHeight);

                        using StringFormat format = new()
                        {
                            Alignment = nextLine == 0 ? StringAlignment.Center : StringAlignment.Near,
                            LineAlignment = StringAlignment.Near,
                            Trimming = StringTrimming.None
                        };
                        e.Graphics.DrawString(line, font, Brushes.Black, lineBounds, format);
                        nextLine++;
                        currentY += lineHeight;
                    }

                    e.HasMorePages = nextLine < lines.Count;
                };

                document.Print();
                return Succeeded("PRINT JOB SENT TO " + printerName);
            }
            catch (Exception ex)
            {
                return Failed("WINDOWS PRINT FAILED: " + ex.Message);
            }
        }

        private static Font CreateWindowsBodyFont(Graphics graphics, float printableWidth, int requestedColumns)
        {
            int columns = NormalizeReceiptColumns(requestedColumns);
            float pointSize = columns > 69 ? 6.5f : 8.0f;
            const float minimumPointSize = 5.0f;

            using StringFormat measureFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
            measureFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            string measurementLine = new string('-', columns);

            while (pointSize > minimumPointSize)
            {
                Font candidate = new("Consolas", pointSize, FontStyle.Regular, GraphicsUnit.Point);
                float measuredWidth = graphics.MeasureString(
                    measurementLine,
                    candidate,
                    int.MaxValue,
                    measureFormat).Width;
                if (measuredWidth <= printableWidth)
                {
                    return candidate;
                }

                candidate.Dispose();
                pointSize -= 0.25f;
            }

            return new Font("Consolas", minimumPointSize, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static DatalinkPrintResult PrintRawEscPos(string printerName, DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            byte[] payload = BuildEscPosPayload(job, settings);
            IntPtr handle = IntPtr.Zero;
            IntPtr unmanaged = IntPtr.Zero;
            bool documentStarted = false;
            bool pageStarted = false;

            try
            {
                if (!OpenPrinter(printerName, out handle, IntPtr.Zero) || handle == IntPtr.Zero)
                {
                    return Failed("RAW PRINTER OPEN FAILED: " + Marshal.GetLastWin32Error());
                }

                DocInfo1 documentInfo = new()
                {
                    DocumentName = "EasyCPDLC ACARS " + job.MessageType,
                    OutputFile = null,
                    DataType = "RAW"
                };

                if (StartDocPrinter(handle, 1, ref documentInfo) == 0)
                {
                    return Failed("RAW PRINT DOCUMENT FAILED: " + Marshal.GetLastWin32Error());
                }
                documentStarted = true;

                if (!StartPagePrinter(handle))
                {
                    return Failed("RAW PRINT PAGE FAILED: " + Marshal.GetLastWin32Error());
                }
                pageStarted = true;

                unmanaged = Marshal.AllocCoTaskMem(payload.Length);
                Marshal.Copy(payload, 0, unmanaged, payload.Length);
                if (!WritePrinter(handle, unmanaged, (uint)payload.Length, out uint written) || written != payload.Length)
                {
                    return Failed("RAW PRINTER WRITE FAILED: " + Marshal.GetLastWin32Error());
                }

                return Succeeded("ESC/POS JOB SENT TO " + printerName);
            }
            catch (Exception ex)
            {
                return Failed("ESC/POS PRINT FAILED: " + ex.Message);
            }
            finally
            {
                if (unmanaged != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(unmanaged);
                }
                if (pageStarted)
                {
                    EndPagePrinter(handle);
                }
                if (documentStarted)
                {
                    EndDocPrinter(handle);
                }
                if (handle != IntPtr.Zero)
                {
                    ClosePrinter(handle);
                }
            }
        }

        internal static byte[] BuildEscPosPayload(DatalinkPrintJob job, DatalinkPrinterSettings settings)
        {
            int columns = NormalizeProfileColumns(settings.Profile, settings.Columns);
            bool condensed = UsesCondensedFont(settings.Profile, columns);
            List<byte> bytes = new();

            Add(bytes, 0x1B, 0x40);             // Initialize.
            Add(bytes, 0x1B, 0x61, 0x00);       // Required default: left alignment.
            Add(bytes, 0x1B, 0x74, 0x00);       // ESC t 0: CP437 on Epson-compatible printers.
            Add(bytes, 0x1B, 0x61, 0x01);       // Center heading only.
            Add(bytes, 0x1B, 0x45, 0x01);       // Bold on.
            Add(bytes, 0x1D, 0x21, 0x11);       // Double width and height.
            AddPrinterText(bytes, "ACARS\n");
            Add(bytes, 0x1D, 0x21, 0x00);       // Normal size.
            Add(bytes, 0x1B, 0x45, 0x00);       // Bold off.
            Add(bytes, 0x1B, 0x61, 0x00);       // Left.
            Add(bytes, 0x1B, 0x4D, condensed ? (byte)0x01 : (byte)0x00); // Font A/B selected by paper profile.

            string receipt = FormatReceiptText(job, columns);
            string body = string.Join("\n", receipt.Split('\n').Skip(1));
            AddPrinterText(bytes, body + "\n");
            Add(bytes, 0x1B, 0x4D, 0x00);       // Restore Font A before feed/cut.

            int feeds = Math.Max(0, Math.Min(12, settings.FeedLines));
            if (feeds > 0)
            {
                Add(bytes, 0x1B, 0x64, (byte)feeds); // Feed n lines.
            }

            if (settings.CutMode == DatalinkCutMode.Partial)
            {
                Add(bytes, 0x1D, 0x56, 0x42, 0x00); // GS V 66 0: partial cut.
            }
            else if (settings.CutMode == DatalinkCutMode.Full)
            {
                Add(bytes, 0x1D, 0x56, 0x41, 0x00); // GS V 65 0: full cut.
            }

            return bytes.ToArray();
        }

        private static DatalinkPrinterStatus StatusFromFlags(uint status, uint jobCount)
        {
            if ((status & PrinterStatusPaperOut) != 0)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "PAPER OUT" };
            }
            if ((status & PrinterStatusDoorOpen) != 0)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "DOOR OPEN" };
            }
            if ((status & PrinterStatusOffline) != 0)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "OFFLINE" };
            }
            if ((status & PrinterStatusPaused) != 0)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "PAUSED" };
            }
            if ((status & (PrinterStatusError | PrinterStatusNotAvailable | PrinterStatusUserIntervention)) != 0)
            {
                return new DatalinkPrinterStatus { IsReady = false, Message = "ATTENTION" };
            }

            return new DatalinkPrinterStatus
            {
                IsReady = true,
                Message = jobCount > 0 ? "READY / " + jobCount + " QUEUED" : "READY"
            };
        }

        private static string ResolvePrinterName(string configuredPrinterName)
        {
            string configured = (configuredPrinterName ?? string.Empty).Trim();

            // Fail closed when no queue has been chosen. The printer selector starts on
            // <SELECT PRINTER> and never persists a queue on its own, so an empty name
            // means the user has not picked one. Auto-selecting the Windows default (or
            // the first installed) printer here silently sent raw ESC/POS control bytes
            // to an unrelated office/laser printer, which rendered them as a garbled
            // multi-page job. Require an explicit selection instead.
            if (string.IsNullOrWhiteSpace(configured))
            {
                return string.Empty;
            }

            // Fail closed when an explicitly selected queue disappears or is renamed.
            // Silently falling back could send an ACARS message to an unrelated printer.
            return GetInstalledPrinterNames()
                .FirstOrDefault(name => string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
        }

        private static int NormalizeReceiptColumns(int requestedColumns)
        {
            return Math.Max(24, Math.Min(104, requestedColumns));
        }

        private static string JoinColumns(string left, string right, int width)
        {
            string cleanLeft = (left ?? string.Empty).TrimEnd();
            string cleanRight = (right ?? string.Empty).Trim();
            if (cleanLeft.Length == 0)
            {
                return cleanRight.Length <= width ? cleanRight.PadLeft(width) : cleanRight;
            }
            if (cleanRight.Length == 0)
            {
                return cleanLeft;
            }

            int gap = width - cleanLeft.Length - cleanRight.Length;
            return gap >= 2
                ? cleanLeft + new string(' ', gap) + cleanRight
                : cleanLeft + "\n" + cleanRight;
        }

        private static string SafeToken(string value, string fallback)
        {
            string cleaned = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim().ToUpperInvariant();
            return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
        }

        private static string Center(string value, int width)
        {
            string cleaned = (value ?? string.Empty).Trim();
            if (cleaned.Length >= width)
            {
                return cleaned.Substring(0, width);
            }

            int left = (width - cleaned.Length) / 2;
            return new string(' ', left) + cleaned;
        }

        internal static IEnumerable<string> WrapLines(string text, int width)
        {
            List<string> output = new();
            foreach (string rawLine in NormalizeLineEndings(text).Split('\n'))
            {
                string remaining = rawLine.TrimEnd();
                if (remaining.Length == 0)
                {
                    output.Add(string.Empty);
                    continue;
                }

                while (remaining.Length > width)
                {
                    int splitAt = remaining.LastIndexOf(' ', width - 1, width);
                    if (splitAt < 1)
                    {
                        splitAt = width;
                        output.Add(remaining.Substring(0, splitAt));
                        remaining = remaining.Substring(splitAt);
                        continue;
                    }

                    output.Add(remaining.Substring(0, splitAt).TrimEnd());
                    // Remove only the single word-separator used for wrapping. Do not
                    // TrimStart: additional leading spaces may be intentional alignment.
                    remaining = remaining.Substring(splitAt + 1);
                }
                output.Add(remaining);
            }
            return output;
        }

        internal static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        internal static byte[] EncodePrinterText(string value)
        {
            return PrinterEncoding.GetBytes(value ?? string.Empty);
        }

        internal static string DeriveStableMessageId(CPDLCMessage message, string aircraftCallsign)
        {
            if (message?.header?.MessageID > 0)
            {
                return "HOPPIE-" + SafeToken(message.type, "TELEX") + "-" +
                    SafeToken(message.recipient, "UNKNOWN") + "-" + message.header.MessageID;
            }

            return DeriveStableMessageId(message?.type, message?.recipient, aircraftCallsign, message?.message);
        }

        internal static string DeriveStableMessageId(string messageType, string sender, string aircraftCallsign, string message)
        {
            string canonical = SafeToken(messageType, "TELEX") + "\n" +
                SafeToken(sender, "UNKNOWN") + "\n" +
                SafeToken(aircraftCallsign, string.Empty) + "\n" +
                NormalizeLineEndings(message).TrimEnd();
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return "SHA256-" + Convert.ToHexString(hash);
        }

        internal static string ToHexDump(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder result = new();
            for (int offset = 0; offset < payload.Length; offset += 16)
            {
                int count = Math.Min(16, payload.Length - offset);
                result.Append(offset.ToString("X4")).Append("  ");
                for (int index = 0; index < 16; index++)
                {
                    result.Append(index < count ? payload[offset + index].ToString("X2") : "  ").Append(' ');
                }
                result.Append(" | ");
                for (int index = 0; index < count; index++)
                {
                    byte value = payload[offset + index];
                    result.Append(value >= 0x20 && value <= 0x7E ? (char)value : '.');
                }
                result.AppendLine();
            }
            return result.ToString();
        }

        private static void Add(List<byte> target, params byte[] values)
        {
            target.AddRange(values);
        }

        private static void AddPrinterText(List<byte> target, string value)
        {
            target.AddRange(EncodePrinterText(value));
        }

        private static string SanitizeFileToken(string value)
        {
            string token = new((value ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
                .Take(48)
                .ToArray());
            return token.Trim('-');
        }

        private static string UniquePath(string basePath, string extension)
        {
            string candidate = basePath + extension;
            for (int index = 1; File.Exists(candidate); index++)
            {
                candidate = basePath + "-" + index + extension;
            }
            return candidate;
        }

        private static DatalinkPrintResult Succeeded(string message)
        {
            return new DatalinkPrintResult { Success = true, Message = message };
        }

        private static DatalinkPrintResult Failed(string message)
        {
            return new DatalinkPrintResult { Success = false, Message = message };
        }
    }
}
