using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EasyCPDLC
{
    /// <summary>
    /// Loadsheets held back for their simulated ground-crew loading time.
    /// </summary>
    /// <remarks>
    /// The first implementation used a fire-and-forget Task.Delay, so a pending
    /// loadsheet was lost whenever the app closed - a 30-minute wait could not survive
    /// even a restart, and the sheet simply never arrived with nothing to show for it.
    /// Pending deliveries are now written to disk and drained by a timer, so they
    /// survive a restart and a sheet whose time elapsed while the app was closed is
    /// delivered on the next launch.
    /// </remarks>
    public partial class MainForm
    {
        private sealed class PendingLoadsheet
        {
            public DateTime DueUtc { get; set; }
            public string Body { get; set; } = string.Empty;
            public string Callsign { get; set; } = string.Empty;
        }

        private static readonly TimeSpan PendingLoadsheetPollInterval = TimeSpan.FromSeconds(10);
        private Timer pendingLoadsheetTimer;

        private static string PendingLoadsheetStorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyCPDLC",
            "pending-loadsheets.json");

        private static List<PendingLoadsheet> ReadPendingLoadsheets()
        {
            try
            {
                string path = PendingLoadsheetStorePath;
                if (!File.Exists(path))
                {
                    return new List<PendingLoadsheet>();
                }

                return JsonConvert.DeserializeObject<List<PendingLoadsheet>>(File.ReadAllText(path))
                    ?? new List<PendingLoadsheet>();
            }
            catch (Exception)
            {
                // A corrupt store must not block the app; start clean.
                return new List<PendingLoadsheet>();
            }
        }

        private static void WritePendingLoadsheets(List<PendingLoadsheet> pending)
        {
            try
            {
                string path = PendingLoadsheetStorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(pending ?? new List<PendingLoadsheet>()));
            }
            catch (Exception ex)
            {
                Logger.Debug("Could not persist pending loadsheets: " + ex.Message);
            }
        }

        /// <summary>Holds a generated loadsheet back for the chosen loading time.</summary>
        internal void SchedulePendingLoadsheet(string body, string messageCallsign, int minutes)
        {
            List<PendingLoadsheet> pending = ReadPendingLoadsheets();
            pending.Add(new PendingLoadsheet
            {
                DueUtc = DateTime.UtcNow.AddMinutes(minutes),
                Body = body ?? string.Empty,
                Callsign = messageCallsign ?? string.Empty
            });
            WritePendingLoadsheets(pending);
            EnsurePendingLoadsheetTimer();
        }

        internal void EnsurePendingLoadsheetTimer()
        {
            if (pendingLoadsheetTimer != null)
            {
                return;
            }

            pendingLoadsheetTimer = new Timer { Interval = (int)PendingLoadsheetPollInterval.TotalMilliseconds };
            pendingLoadsheetTimer.Tick += (_, __) => DeliverDuePendingLoadsheets();
            pendingLoadsheetTimer.Start();

            // Anything already due (including a wait that elapsed while the app was
            // closed) is delivered straight away rather than after the first interval.
            DeliverDuePendingLoadsheets();
        }

        private void DeliverDuePendingLoadsheets()
        {
            List<PendingLoadsheet> pending = ReadPendingLoadsheets();
            if (pending.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            List<PendingLoadsheet> due = pending.Where(item => item.DueUtc <= now).ToList();
            if (due.Count == 0)
            {
                return;
            }

            // Persist the remainder first: a failure while writing a message must not
            // leave a delivered sheet queued to arrive again on the next tick.
            WritePendingLoadsheets(pending.Where(item => item.DueUtc > now).ToList());

            foreach (PendingLoadsheet item in due)
            {
                try
                {
                    WriteMessage(
                        DatalinkPrinter.NormalizeLineEndings(item.Body).Trim(),
                        "LOADSHEET",
                        "ELOADCONTROL",
                        false,
                        null,
                        "ELOADCONTROL API",
                        item.Callsign,
                        true);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Pending loadsheet delivery failed");
                }
            }
        }
    }
}
