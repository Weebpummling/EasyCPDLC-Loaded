using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyCPDLC.VNS430
{
    /// <summary>
    /// Private SimConnect Client Data transport for the EasyCPDLC VNS430 bridge.
    /// It neither maps nor subscribes to any aircraft or Garmin simulator event.
    /// </summary>
    internal sealed class Vns430CompanionInput : IDisposable
    {
        private const uint CommandClientDataId = 1;
        private const uint StatusClientDataId = 2;
        private const uint CommandDefinitionId = 1;
        private const uint StatusDefinitionId = 2;
        private const uint CommandRequestId = 1;
        private const uint SimConnectRecvClientDataId = 16;
        private const uint ClientDataPeriodOnSet = 3;
        private const uint ClientDataRequestChanged = 1;
        private const int ClientDataPayloadOffset = 40;
        private const uint CommBusBroadcastToJs = 1;
        private const string DisplayCommBusEvent = "EasyCPDLC.VNS430.Display.v1";

        private readonly DispatchProc dispatchProc;
        private IntPtr connection;
        private uint lastCommandSequence;
        private uint statusSequence;
        private DateTime lastModulePacketUtc = DateTime.MinValue;
        private DateTime lastStatusSentUtc = DateTime.MinValue;
        private DateTime lastDisplaySentUtc = DateTime.MinValue;
        private ulong lastDisplayHash;

        [StructLayout(LayoutKind.Sequential)]
        private struct SimConnectRecv
        {
            internal uint Size;
            internal uint Version;
            internal uint Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SimConnectRecvClientData
        {
            internal SimConnectRecv Header;
            internal uint RequestId;
            internal uint ObjectId;
            internal uint DefineId;
            internal uint Flags;
            internal uint EntryNumber;
            internal uint OutOf;
            internal uint DefineCount;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DispatchProc(IntPtr data, uint dataSize, IntPtr context);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_Open(
            out IntPtr handle,
            string name,
            IntPtr windowHandle,
            uint userMessage,
            IntPtr eventHandle,
            uint configIndex);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_Close(IntPtr handle);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_MapClientDataNameToID(IntPtr handle, string name, uint clientDataId);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_CreateClientData(IntPtr handle, uint clientDataId, uint size, uint flags);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_AddToClientDataDefinition(
            IntPtr handle,
            uint definitionId,
            uint offset,
            uint sizeOrType,
            float epsilon,
            uint datumId);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_RequestClientData(
            IntPtr handle,
            uint clientDataId,
            uint requestId,
            uint definitionId,
            uint period,
            uint flags,
            uint origin,
            uint interval,
            uint limit);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_SetClientData(
            IntPtr handle,
            uint clientDataId,
            uint definitionId,
            uint flags,
            uint reserved,
            uint unitSize,
            ref Vns430CompanionStatusPacket data);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_CallDispatch(IntPtr handle, DispatchProc callback, IntPtr context);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_CallCommBusEvent(
            IntPtr handle,
            string eventName,
            uint broadcastTo,
            uint bufferSize,
            byte[] data);

        internal Vns430CompanionInput()
        {
            dispatchProc = Dispatch;
        }

        internal bool Enabled => connection != IntPtr.Zero;
        internal bool ModuleActive => Enabled && DateTime.UtcNow - lastModulePacketUtc < TimeSpan.FromSeconds(3);
        internal string Status { get; private set; } = "OFF";
        internal event Action<Vns430Command> CommandReceived;

        internal bool TryEnable(IntPtr windowHandle, uint messageId, out string error)
        {
            error = string.Empty;
            if (Enabled)
            {
                return true;
            }

            try
            {
                int result = SimConnect_Open(
                    out connection,
                    "EasyCPDLC companion transport",
                    windowHandle,
                    messageId,
                    IntPtr.Zero,
                    0);

                if (result < 0 || connection == IntPtr.Zero)
                {
                    error = "MSFS 2024 is not available through SimConnect.";
                    if (connection != IntPtr.Zero)
                    {
                        // A failure code can still hand back a live handle. Dropping the
                        // reference without closing it leaks the handle, and the panel
                        // retries every five seconds for the whole session.
                        try { SimConnect_Close(connection); } catch { /* Shutdown only. */ }
                        connection = IntPtr.Zero;
                    }

                    Status = "OFFLINE";
                    return false;
                }

                uint commandSize = (uint)Marshal.SizeOf<Vns430CompanionCommandPacket>();
                uint statusSize = (uint)Marshal.SizeOf<Vns430CompanionStatusPacket>();

                if (SimConnect_MapClientDataNameToID(connection, Vns430CompanionProtocol.CommandClientDataName, CommandClientDataId) < 0 ||
                    SimConnect_MapClientDataNameToID(connection, Vns430CompanionProtocol.StatusClientDataName, StatusClientDataId) < 0 ||
                    SimConnect_AddToClientDataDefinition(connection, CommandDefinitionId, 0, commandSize, 0, 0) < 0 ||
                    SimConnect_AddToClientDataDefinition(connection, StatusDefinitionId, 0, statusSize, 0, 0) < 0)
                {
                    error = "The EasyCPDLC companion data channel could not be registered.";
                    Disable();
                    Status = "OFFLINE";
                    return false;
                }

                // Either endpoint may start first. Creation failures are harmless when the
                // named area already exists with the same size.
                SimConnect_CreateClientData(connection, CommandClientDataId, commandSize, 0);
                SimConnect_CreateClientData(connection, StatusClientDataId, statusSize, 0);

                if (SimConnect_RequestClientData(
                    connection,
                    CommandClientDataId,
                    CommandRequestId,
                    CommandDefinitionId,
                    ClientDataPeriodOnSet,
                    ClientDataRequestChanged,
                    0,
                    0,
                    0) < 0)
                {
                    error = "The EasyCPDLC companion command channel could not be opened.";
                    Disable();
                    Status = "OFFLINE";
                    return false;
                }

                lastCommandSequence = 0;
                lastModulePacketUtc = DateTime.MinValue;
                Status = "WAITING";
                return true;
            }
            catch (DllNotFoundException)
            {
                error = "SimConnect.dll was not found. Start MSFS 2024 and install the EasyCPDLC companion package.";
            }
            catch (BadImageFormatException)
            {
                error = "The installed SimConnect runtime does not match this EasyCPDLC build.";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Disable();
            Status = "OFFLINE";
            return false;
        }

        internal void ReceiveWindowMessage()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                SimConnect_CallDispatch(connection, dispatchProc, IntPtr.Zero);
            }
            catch
            {
                Disable();
                Status = "LOST";
            }
        }

        internal void UpdateStatus(Vns430BackendSnapshot snapshot, Vns430Page page, bool cursorActive, bool dcduMode, uint lampFlags = 0)
        {
            if (!Enabled)
            {
                return;
            }

            Status = ModuleActive ? "ACTIVE" : "WAITING";
            if (DateTime.UtcNow - lastStatusSentUtc < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            uint flags = Vns430CompanionProtocol.StatusAppOnline;
            if (snapshot?.Connected == true)
            {
                flags |= Vns430CompanionProtocol.StatusVatsimConnected;
            }
            if (cursorActive)
            {
                flags |= Vns430CompanionProtocol.StatusCursorActive;
            }
            if (dcduMode)
            {
                flags |= Vns430CompanionProtocol.StatusDcduMode;
            }

            // CDU annunciator / EXEC lamp bits, supplied by the CDU front end.
            flags |= lampFlags;

            Vns430CompanionStatusPacket packet = new()
            {
                Magic = Vns430CompanionProtocol.Magic,
                Version = Vns430CompanionProtocol.Version,
                Sequence = ++statusSequence,
                Flags = flags,
                UnreadCount = (uint)(snapshot?.Messages.Count(message => message.Unread) ?? 0),
                Page = (uint)page
            };

            try
            {
                uint size = (uint)Marshal.SizeOf<Vns430CompanionStatusPacket>();
                if (SimConnect_SetClientData(connection, StatusClientDataId, StatusDefinitionId, 0, 0, size, ref packet) >= 0)
                {
                    lastStatusSentUtc = DateTime.UtcNow;
                }
            }
            catch
            {
                Disable();
                Status = "LOST";
            }
        }

        internal void UpdateDisplay(Bitmap display)
        {
            if (!Enabled || display == null)
            {
                return;
            }

            try
            {
                // Encoding a 240x128 PNG and base64-ing it costs ~0.56 ms and ~47 KB.
                // The panel is static most of the time, so hash the raster first and
                // only pay for the encode when the frame actually changed. The
                // one-second keepalive still resends an unchanged frame so a gauge
                // that connects late is not left blank.
                ulong frame = HashRaster(display);
                if (frame == lastDisplayHash &&
                    DateTime.UtcNow - lastDisplaySentUtc < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                using MemoryStream stream = new();
                display.Save(stream, ImageFormat.Png);
                string image = Convert.ToBase64String(stream.ToArray());

                string json =
                    "{\"version\":1,\"width\":240,\"height\":128,\"png\":\"data:image/png;base64," +
                    image +
                    "\"}";
                byte[] payload = Encoding.UTF8.GetBytes(json + "\0");
                if (SimConnect_CallCommBusEvent(
                    connection,
                    DisplayCommBusEvent,
                    CommBusBroadcastToJs,
                    (uint)payload.Length,
                    payload) < 0)
                {
                    Status = "DISPLAY ERROR";
                }
                else
                {
                    lastDisplayHash = frame;
                    lastDisplaySentUtc = DateTime.UtcNow;
                }
            }
            catch
            {
                Disable();
                Status = "LOST";
            }
        }

        private static ulong HashRaster(Bitmap display)
        {
            Rectangle bounds = new(0, 0, display.Width, display.Height);
            BitmapData data = display.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                ulong hash = 14695981039346656037UL;
                int words = display.Width * display.Height;
                unchecked
                {
                    for (int i = 0; i < words; i++)
                    {
                        hash = (hash ^ (uint)Marshal.ReadInt32(data.Scan0, i * 4)) * 1099511628211UL;
                    }
                }

                return hash;
            }
            finally
            {
                display.UnlockBits(data);
            }
        }

        private void Dispatch(IntPtr data, uint dataSize, IntPtr context)
        {
            int packetSize = Marshal.SizeOf<Vns430CompanionCommandPacket>();
            if (data == IntPtr.Zero || dataSize < ClientDataPayloadOffset + packetSize)
            {
                return;
            }

            SimConnectRecvClientData received = Marshal.PtrToStructure<SimConnectRecvClientData>(data);
            if (received.Header.Id != SimConnectRecvClientDataId || received.RequestId != CommandRequestId)
            {
                return;
            }

            Vns430CompanionCommandPacket packet =
                Marshal.PtrToStructure<Vns430CompanionCommandPacket>(IntPtr.Add(data, ClientDataPayloadOffset));
            if (!Vns430CompanionProtocol.TryReadCommand(packet, out Vns430Command command))
            {
                return;
            }

            lastModulePacketUtc = DateTime.UtcNow;
            Status = "ACTIVE";
            if (packet.Sequence == lastCommandSequence)
            {
                return;
            }

            lastCommandSequence = packet.Sequence;
            if (command != Vns430Command.None)
            {
                CommandReceived?.Invoke(command);
            }
        }

        internal void Disable()
        {
            if (connection != IntPtr.Zero)
            {
                try
                {
                    SimConnect_Close(connection);
                }
                catch
                {
                    // Shutdown only.
                }
            }

            connection = IntPtr.Zero;
            lastModulePacketUtc = DateTime.MinValue;
            lastDisplaySentUtc = DateTime.MinValue;
            lastDisplayHash = 0;
            Status = "OFF";
        }

        public void Dispose()
        {
            Disable();
        }
    }
}
