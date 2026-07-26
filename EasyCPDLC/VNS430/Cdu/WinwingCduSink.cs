using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyCPDLC.VNS430.Cdu
{
    // Which physical CDU the frames are addressed to. The unit's seat is set in WinWing
    // SimAppPro (each seat enumerates as its own USB product id), and MobiFlight serves a
    // separate websocket path per seat, so this has to match what SimAppPro is set to.
    internal enum WinwingSeat
    {
        Off,
        Captain,
        FirstOfficer,
        Observer
    }

    /// <summary>
    /// Streams the CDU screen to a WinWing CDU through MobiFlight.
    /// </summary>
    /// <remarks>
    /// MobiFlight owns the USB device (SimAppPro must be closed) and hosts a websocket
    /// server on port 8320 with one endpoint per seat. We are a client of that server, so
    /// there is no contention for the hardware: MobiFlight drives the panel over HID and we
    /// simply hand it frames.
    ///
    /// Push() is called from the paint path, so it never blocks or does I/O: it stores the
    /// latest frame and a background pump sends it. Frames are coalesced — if paints outrun
    /// the socket, only the newest frame is sent, which is what a display wants anyway.
    /// </remarks>
    internal sealed class WinwingCduSink : ICduDisplaySink, IDisposable
    {
        private const string Host = "ws://localhost:8320/winwing/";
        private const string FontName = "Boeing";   // 737 CDU/PFP typeface shipped by MobiFlight
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(50);

        // A transient hiccup on an established link (MobiFlight briefly not reading
        // while it writes to the device, a single failed send) must not cost the full
        // RetryDelay - that showed up as the display freezing for ~5 s. The first few
        // failures retry almost immediately; only a persistently dead server (MobiFlight
        // closed) falls back to the slow cadence. Sends and connects are also bounded,
        // so a hung socket can never stall the pump indefinitely.
        private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(250);
        private const int FastRetryLimit = 3;
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

        // Generous on purpose. MobiFlight has to claim the CDU over HID before it
        // completes the websocket upgrade, which can take several seconds on a cold
        // device. A tight timeout here aborts mid-handshake and retries forever, which
        // looks exactly like "the link never connects" while piling up half-open
        // sockets - the connection is only bounded so a dead server cannot wedge us.
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
        private int consecutiveFailures;

        // Selecting a font makes MobiFlight write glyph bitmaps into the device's flash.
        // That must happen at most once per run: re-sending it on every reconnect (which
        // happens every RetryDelay while MobiFlight is closed) means repeated flash writes,
        // and an interrupted one leaves the font partially written - which shows up as
        // specific letters missing on the panel. Static, so reconnects and seat changes
        // within one session never trigger a second write.
        private static int fontUploaded;
        private static readonly TimeSpan FontSettleDelay = TimeSpan.FromSeconds(3);

        private readonly Uri endpoint;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task pump;
        private ClientWebSocket socket;
        private IReadOnlyList<object[]> pending;
        private volatile bool connected;

        internal WinwingCduSink(WinwingSeat seat)
        {
            Seat = seat;
            endpoint = new Uri(Host + PathFor(seat));
            pump = Task.Run(() => PumpAsync(cancellation.Token));
        }

        internal WinwingSeat Seat { get; }

        // True once frames are actually going out, so the UI can show more than "selected".
        internal bool Connected => connected;

        internal static string PathFor(WinwingSeat seat) => seat switch
        {
            WinwingSeat.FirstOfficer => "cdu-co-pilot",
            WinwingSeat.Observer => "cdu-observer",
            _ => "cdu-captain"
        };

        internal static string Label(WinwingSeat seat) => seat switch
        {
            WinwingSeat.Captain => "CAPT",
            WinwingSeat.FirstOfficer => "FO",
            WinwingSeat.Observer => "OBS",
            _ => "OFF"
        };

        internal static WinwingSeat ParseSeat(string value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "CAPT" => WinwingSeat.Captain,
            "FO" => WinwingSeat.FirstOfficer,
            "OBS" => WinwingSeat.Observer,
            _ => WinwingSeat.Off
        };

        public void Push(IReadOnlyList<object[]> cells)
        {
            // Newest frame wins; never block the paint thread.
            Interlocked.Exchange(ref pending, cells);
        }

        private async Task PumpAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                IReadOnlyList<object[]> frame = null;
                try
                {
                    if (socket == null || socket.State != WebSocketState.Open)
                    {
                        await ConnectAsync(token).ConfigureAwait(false);
                    }

                    frame = Interlocked.Exchange(ref pending, null);
                    if (frame == null)
                    {
                        await Task.Delay(IdlePoll, token).ConfigureAwait(false);
                        continue;
                    }

                    await SendAsync(new { Target = "Display", Data = frame }, token).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                // Only a real shutdown ends the pump. A send/connect TIMEOUT also
                // surfaces as OperationCanceledException, and must fall through to the
                // failure path below instead of silently killing the display forever.
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Put the consumed frame back (unless a newer one arrived) so the
                    // reconnect resends the current screen instead of leaving it stale.
                    if (frame != null)
                    {
                        Interlocked.CompareExchange(ref pending, frame, null);
                    }

                    // Fast retry is for a hiccup on a link that was already up. A
                    // connect that never completed must back off instead, or we hammer
                    // a server that is not ready and leave a pile of half-open sockets.
                    bool hadLink = connected;
                    DropSocket();
                    consecutiveFailures += 1;
                    TimeSpan delay = hadLink && consecutiveFailures <= FastRetryLimit
                        ? FastRetryDelay
                        : RetryDelay;
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            DropSocket();
        }

        private async Task ConnectAsync(CancellationToken token)
        {
            DropSocket();

            // Publish the socket BEFORE connecting. Assigning it only after the await
            // meant a connect that was cancelled (seat change, shutdown) or timed out
            // left a live socket that DropSocket could never reach - it leaked as an
            // established connection to MobiFlight, and the stale clients competed with
            // the real one for the device, so the display stopped updating while the
            // keys (a different transport) kept working.
            ClientWebSocket next = new();
            socket = next;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await next.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            }

            // Select the font once per run only (see fontUploaded above), and give the
            // flash write time to finish before the first frame. Interrupting it is what
            // corrupts individual glyphs.
            if (Interlocked.Exchange(ref fontUploaded, 1) == 0)
            {
                await SendAsync(new { Target = "Font", Data = FontName }, token).ConfigureAwait(false);
                await Task.Delay(FontSettleDelay, token).ConfigureAwait(false);
            }

            connected = true;
        }

        private async Task SendAsync(object message, CancellationToken token)
        {
            ClientWebSocket current = socket;
            if (current == null || current.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WinWing socket is not open.");
            }

            byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));

            // Bounded: a peer that stops reading (device busy) must fail the send and
            // take the fast-retry path, never wedge the pump on a full TCP buffer.
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SendTimeout);
            await current
                .SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token)
                .ConfigureAwait(false);
        }

        private void DropSocket()
        {
            connected = false;
            ClientWebSocket current = Interlocked.Exchange(ref socket, null);
            if (current == null)
            {
                return;
            }

            try
            {
                current.Abort();
            }
            catch (Exception)
            {
                // Nothing useful to do; the socket is being discarded either way.
            }
            current.Dispose();
        }

        public void Dispose()
        {
            try
            {
                // Abort first: cancelling alone leaves an in-flight connect or send to
                // unwind on its own, and the pump is not waited on for long. Aborting
                // the published socket guarantees the connection is torn down now
                // rather than lingering as a stale client on MobiFlight.
                cancellation.Cancel();
                DropSocket();

                // Do not block the UI thread on a socket that may be mid-retry.
                pump?.Wait(TimeSpan.FromMilliseconds(250));
            }
            catch (Exception)
            {
                // Shutdown is best-effort.
            }
            finally
            {
                DropSocket();
                cancellation.Dispose();
            }
        }
    }
}
