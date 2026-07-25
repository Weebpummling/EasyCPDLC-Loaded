using System;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace EasyCPDLC.VNS430
{
    internal sealed class Vns430Preferences
    {
        public bool CompanionModuleEnabled { get; set; } = true;
        public bool DcduCompanionMode { get; set; }

        // Screen mode. With hardware buttons driving input, show the panel as a
        // bare LCD behind the physical unit: this hides the photographed bezel and
        // disables the on-screen click zones together.
        public bool ScreenOnlyMode { get; set; }

        public int Left { get; set; } = -1;
        public int Top { get; set; } = -1;
        // Panel aspect is 960 x 407. The old 960 x 455 default dated from when the form
        // had a title bar and border; the form is borderless now, so window size is the
        // client size and 455 produced a squashed panel on first launch.
        public int Width { get; set; } = 1344;
        public int Height { get; set; } = 570;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyCPDLC",
            "vns430.json");

        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyCPDLC",
            "gns430.json");

        internal static Vns430Preferences Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<Vns430Preferences>(File.ReadAllText(SettingsPath))
                        ?? new Vns430Preferences();
                }

                // Carry existing personal layout/module choices across the product rename.
                if (File.Exists(LegacySettingsPath))
                {
                    return JsonSerializer.Deserialize<Vns430Preferences>(File.ReadAllText(LegacySettingsPath))
                        ?? new Vns430Preferences();
                }
            }
            catch
            {
                // A damaged personal preference file must never prevent EasyCPDLC from starting.
            }

            return new Vns430Preferences();
        }

        internal void Save(Rectangle bounds)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;

            try
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch
            {
                // Preferences are optional; the panel remains usable with defaults.
            }
        }
    }
}
