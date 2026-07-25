using EasyCPDLC.VNS430;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace EasyCPDLC.Tests
{
    public sealed class Vns430Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(18)]
        [InlineData(19)]
        [InlineData(38)]
        public void CompanionPackets_ValidateEveryDefinedCommand(byte value)
        {
            Vns430CompanionCommandPacket packet = new()
            {
                Magic = Vns430CompanionProtocol.Magic,
                Version = Vns430CompanionProtocol.Version,
                Sequence = 27,
                Command = value,
                Checksum = Vns430CompanionProtocol.CalculateCommandChecksum(27, value)
            };

            Assert.True(Vns430CompanionProtocol.TryReadCommand(packet, out Vns430Command command));
            Assert.Equal((Vns430Command)value, command);
        }

        // The companion gate forwards one contiguous block to the CDU backend:
        // DcduLeftLsk1..CduBrightnessDown. If anything in that span stops being a CDU
        // input, or the block stops being contiguous, that gate silently drops keys -
        // which is exactly how the whole 737 keypad once became dead over hardware while
        // the line-select keys kept working.
        [Fact]
        public void CduInputCommands_AreOneContiguousBlockEndingAtBrightnessDown()
        {
            const int first = (int)Vns430Command.DcduLeftLsk1;
            const int last = (int)Vns430Command.CduBrightnessDown;

            Assert.Equal(19, first);
            Assert.Equal(97, last);

            // The keypad must sit inside the forwarded range, not past its end.
            Assert.InRange((int)Vns430Command.CduAlphaA, first, last);
            Assert.InRange((int)Vns430Command.CduExec, first, last);
            Assert.InRange((int)Vns430Command.CduMenu, first, last);
            Assert.InRange((int)Vns430Command.DcduHide, first, last);

            // Every value in the block must survive protocol validation. The alpha and
            // digit keys are declared only by their range endpoints, so Enum.IsDefined is
            // not sufficient here - that gap silently rejected B..Y and 1..8 from hardware.
            for (int value = first; value <= last; value++)
            {
                Assert.True(
                    Vns430CompanionProtocol.IsKnownCommand((byte)value),
                    $"Command {value} is inside the CDU input block but fails validation.");
            }
        }

        // End-to-end guard: a packet carrying any keypad key must decode. 'B' (40) and
        // '5' (70) are the cases the endpoint-only enum used to throw away.
        [Theory]
        [InlineData(40)]   // B
        [InlineData(63)]   // Y
        [InlineData(70)]   // 5
        [InlineData(91)]   // EXEC
        public void CompanionPackets_AcceptEveryKeypadKey(byte value)
        {
            Vns430CompanionCommandPacket packet = new()
            {
                Magic = Vns430CompanionProtocol.Magic,
                Version = Vns430CompanionProtocol.Version,
                Sequence = 5,
                Command = value,
                Checksum = Vns430CompanionProtocol.CalculateCommandChecksum(5, value)
            };

            Assert.True(Vns430CompanionProtocol.TryReadCommand(packet, out Vns430Command command));
            Assert.Equal((Vns430Command)value, command);
        }

        [Fact]
        public void CompanionPackets_RejectDamageAndUnknownCommands()
        {
            Vns430CompanionCommandPacket packet = new()
            {
                Magic = Vns430CompanionProtocol.Magic,
                Version = Vns430CompanionProtocol.Version,
                Sequence = 5,
                Command = 99,
                Checksum = Vns430CompanionProtocol.CalculateCommandChecksum(5, 99)
            };

            Assert.False(Vns430CompanionProtocol.TryReadCommand(packet, out _));

            packet.Command = (uint)Vns430Command.Menu;
            packet.Checksum = 0;
            Assert.False(Vns430CompanionProtocol.TryReadCommand(packet, out _));
        }

        [Fact]
        public void CompanionProtocol_HasStableCrossLanguageLayoutAndPrivateNames()
        {
            Assert.Equal(20, Marshal.SizeOf<Vns430CompanionCommandPacket>());
            Assert.Equal(32, Marshal.SizeOf<Vns430CompanionStatusPacket>());
            Assert.StartsWith("EasyCPDLC.", Vns430CompanionProtocol.CommandClientDataName);
            Assert.Equal("EASYCPDLC_VNS_COMMAND", Vns430CompanionProtocol.CommandLVar);
            Assert.DoesNotContain("GPS_", Vns430CompanionProtocol.CommandClientDataName);
            Assert.DoesNotContain("AS430", Vns430CompanionProtocol.CommandClientDataName);
            Assert.Equal(1u << 3, Vns430CompanionProtocol.StatusDcduMode);
            Assert.Equal("EASYCPDLC_DCDU_MODE", Vns430CompanionProtocol.DcduModeLVar);
        }

        [Fact]
        public void CompanionModule_DcduInputsArePrivateAndModeGated()
        {
            string source = File.ReadAllText(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "EasyCPDLC", "VNS430", "MSFS2024Module", "Bridge", "Sources", "EasyCpdlcVnsModule.cpp")));

            Assert.Equal(12, Regex.Matches(source, @"EASYCPDLC_DCDU_LSK_[LR][1-6]").Count);
            Assert.Contains("EASYCPDLC_DCDU_CONNECT", source);
            Assert.Contains("EASYCPDLC_DCDU_REPRINT", source);
            Assert.Contains("if (!g_dcduMode)", source);
            Assert.Contains("if (!g_dcduMode && command >= 1 && command <= 18)", source);
            // Ids from register_named_variable go stale when a flight load rebuilds the
            // L-var table; the module must re-bind before reading or writing through them.
            Assert.Contains("EnsureLVarBindings();", source);

            // Every extern "C" declaration becomes a hard WASM import, and an import the
            // standalone host does not export stops the module instantiating - it loads
            // as nothing at all, reported as "MobiFlight module: not detected". Adding
            // check_named_variable did exactly that. Pin the import list.
            string externBlock = Regex.Match(source, @"extern ""C""\s*\{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
            Assert.NotEmpty(externBlock);
            string[] hostImports = Regex.Matches(externBlock, @"(\w+)\s*\(")
                .Select(match => match.Groups[1].Value)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[] { "get_named_variable_value", "register_named_variable", "set_named_variable_value" },
                hostImports);
            // Hardware keys are polled per visual frame, not on the one-second heartbeat.
            Assert.Contains("\"Frame\"", source);
            // The Frame event arrives under its own receive id with a wider struct, so
            // handling it as a plain SIMCONNECT_RECV_ID_EVENT silently never fires.
            Assert.Contains("SIMCONNECT_RECV_ID_EVENT_FRAME", source);
            Assert.Contains("SIMCONNECT_RECV_EVENT_FRAME", source);
            // The fast-poll subscriptions are optional and MUST stay out of the fatal
            // setup chain. Chaining "Frame" into it meant a host that refuses the event
            // tore down the whole SimConnect channel: the module loaded and then sat
            // idle for ever, reported as "MobiFlight module: not detected".
            string fatalChain = Regex.Match(
                source,
                @"if \(SimConnect_SubscribeToSystemEvent.*?\n        \}",
                RegexOptions.Singleline).Value;
            Assert.Contains("\"1sec\"", fatalChain);
            Assert.DoesNotContain("\"Frame\"", fatalChain);
            Assert.DoesNotContain("\"6Hz\"", fatalChain);

            // The one-second tick polls too, so losing the fast events costs cadence,
            // never input.
            Assert.Matches(@"void ProcessTick\(float elapsed\)[\s\S]*?PollInputs\(\);", source);
            Assert.DoesNotContain("AS430_", source);
            Assert.DoesNotContain("EASYCPDLC_GNS_", source);
            Assert.DoesNotContain(">K:", source);
        }

        [Theory]
        [InlineData("123456", "secret", true)]
        [InlineData("", "", true)]
        [InlineData("ABC", "secret", false)]
        [InlineData("-1", "secret", false)]
        public void TrayCredentialEditor_ValidatesCid(string cid, string hoppie, bool expected)
        {
            Assert.Equal(expected, CredentialSettingsForm.TryValidate(cid, hoppie, out _, out _));
        }

        [Fact]
        public void MobiFlightCompanionProfile_UsesOnlyThePrivateModuleLVar()
        {
            string profile = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "MobiFlight",
                "EasyCPDLC-VNS430-Module.mfproj"));

            using JsonDocument document = JsonDocument.Parse(profile);
            Assert.Equal("EasyCPDLC VNS430 Module", document.RootElement.GetProperty("Name").GetString());
            Assert.Equal(18, Regex.Matches(profile, @"\(>L:EASYCPDLC_VNS_COMMAND\)").Count);
            Assert.Equal(18, Regex.Matches(profile, @"MSFS2020CustomInputAction").Count);
            Assert.DoesNotContain("KeyInputAction", profile);
            Assert.DoesNotContain("AS430_", profile);
            Assert.DoesNotContain("GPS_", profile);
            Assert.DoesNotContain("EASYCPDLC_GNS_", profile);
            Assert.DoesNotContain(">K:", profile);
        }

        [Fact]
        public void RepositoryLayout_NestsVns430UnderPrintElcWithoutDuplicateGuides()
        {
            string repository = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", ".."));
            string vnsRoot = Path.Combine(repository, "EasyCPDLC", "VNS430");

            Assert.True(Directory.Exists(vnsRoot));
            Assert.True(Directory.Exists(Path.Combine(vnsRoot, "MSFS2024Module")));
            Assert.True(Directory.Exists(Path.Combine(vnsRoot, "Docs", "images")));
            Assert.False(Directory.Exists(Path.Combine(repository, "EasyCPDLC", "GNS430")));
            Assert.False(Directory.Exists(Path.Combine(vnsRoot, "Tutorial")));
            Assert.False(Directory.Exists(Path.Combine(vnsRoot, "Screenshots")));
            Assert.Equal(2, Directory.GetFiles(vnsRoot, "README.md", SearchOption.AllDirectories).Length);

            // The in-simulator 3D instrument was removed in favour of a desktop
            // screen driven by physical buttons. Only the WASM bridge (used for
            // keybinds) and the MobiFlight profiles remain in the module.
            string moduleRoot = Path.Combine(vnsRoot, "MSFS2024Module");
            Assert.False(File.Exists(Path.Combine(moduleRoot, "Build-Package.ps1")));
            Assert.False(File.Exists(Path.Combine(moduleRoot, "Validate-Package.ps1")));
            Assert.False(Directory.Exists(Path.Combine(moduleRoot, "PackageSources")));
            Assert.True(File.Exists(Path.Combine(moduleRoot, "Bridge", "Build-Wasm.ps1")));
            Assert.True(File.Exists(Path.Combine(moduleRoot, "MobiFlight", "EasyCPDLC-VNS430-Module.mfproj")));
        }

        [Fact]
        public void MobiFlightDcduProfile_ProvidesTwentyModeGatedPrivateInputs()
        {
            string profile = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "MobiFlight",
                "EasyCPDLC-DCDU-Module.mfproj"));

            using JsonDocument document = JsonDocument.Parse(profile);
            Assert.Equal("EasyCPDLC DCDU Module", document.RootElement.GetProperty("Name").GetString());
            Assert.Equal(20, Regex.Matches(profile, @"\(>L:EASYCPDLC_DCDU_").Count);
            Assert.Equal(20, Regex.Matches(profile, @"MSFS2020CustomInputAction").Count);
            Assert.Equal(12, Regex.Matches(profile, @"EASYCPDLC_DCDU_LSK_[LR][1-6]").Count / 2);
            Assert.DoesNotContain("EASYCPDLC_VNS_COMMAND", profile);
            Assert.DoesNotContain("KeyInputAction", profile);
            Assert.DoesNotContain("AS430_", profile);
            Assert.DoesNotContain("GPS_", profile);
            Assert.DoesNotContain(">K:", profile);
        }

        [Theory]
        [InlineData(-1, 4, 3)]
        [InlineData(4, 4, 0)]
        [InlineData(7, 4, 3)]
        public void EncoderSelection_WrapsLikeThePhysicalUnit(int value, int count, int expected)
        {
            Assert.Equal(expected, Vns430Form.Wrap(value, count));
        }

        [Fact]
        public void MessageText_IsCollapsedAndWrappedForTheSmallDisplay()
        {
            Assert.Equal("CLIMB TO FL350 NOW", Vns430Form.CollapseWhitespace("  CLIMB\r\n TO   FL350 NOW "));
            Assert.Equal(new[] { "CLIMB TO", "FL350 NOW" }, Vns430Form.WrapText("CLIMB TO FL350 NOW", 9).ToArray());
        }

        [Theory]
        [InlineData((int)Vns430Page.Messages, true)]
        [InlineData((int)Vns430Page.MessageDetail, true)]
        [InlineData((int)Vns430Page.Status, false)]
        [InlineData((int)Vns430Page.Menu, false)]
        public void MessageSelection_IsPreservedOnlyAcrossListAndDetailPages(int page, bool expected)
        {
            Assert.Equal(expected, Vns430Form.ShouldPreserveMessageSelection((Vns430Page)page));
        }

        [Theory]
        [InlineData((int)Vns430PageGroup.Nav, (int)Vns430Page.Status, 2)]
        [InlineData((int)Vns430PageGroup.Wpt, (int)Vns430Page.Logon, 3)]
        [InlineData((int)Vns430PageGroup.Aux, (int)Vns430Page.AocMenu, 3)]
        [InlineData((int)Vns430PageGroup.Nrst, (int)Vns430Page.Messages, 1)]
        public void PageGroups_FollowThePhysicalLargeAndSmallKnobModel(int group, int firstPage, int count)
        {
            Vns430Page[] pages = Vns430Form.PagesForGroup((Vns430PageGroup)group);
            Assert.Equal(count, pages.Length);
            Assert.Equal((Vns430Page)firstPage, pages[0]);
        }

        [Fact]
        public void LcdRenderer_UsesTheNative240By128RasterAndSampledPalette()
        {
            Vns430LcdState state = new()
            {
                Snapshot = new Vns430BackendSnapshot
                {
                    Connected = true,
                    Callsign = "N731CD",
                    CurrentAtcUnit = "KZAK",
                    Messages = new List<Vns430MessageSnapshot>()
                },
                Page = Vns430Page.Status,
                PageGroup = Vns430PageGroup.Nav,
                CursorActive = true
            };

            // The palette is asserted on the raw raster. The older-LCD appearance pass
            // blends every pixel, so exact sampled colours only survive before it runs.
            using Bitmap image = Vns430LcdRenderer.Render(state, applyAppearance: false);
            Assert.Equal(240, image.Width);
            Assert.Equal(128, image.Height);
            HashSet<int> colors = new();
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    colors.Add(image.GetPixel(x, y).ToArgb());
                }
            }
            Assert.Contains(Vns430LcdRenderer.Blue.ToArgb(), colors);
            Assert.Contains(Vns430LcdRenderer.Black.ToArgb(), colors);
            Assert.Contains(Vns430LcdRenderer.Cyan.ToArgb(), colors);
            Assert.Contains(Vns430LcdRenderer.Green.ToArgb(), colors);
        }

        [Fact]
        public void LcdRenderer_AppliesTheOlderDisplayAppearanceByDefault()
        {
            Vns430LcdState state = new()
            {
                Snapshot = new Vns430BackendSnapshot
                {
                    Connected = true,
                    Callsign = "N731CD",
                    CurrentAtcUnit = "KZAK",
                    Messages = new List<Vns430MessageSnapshot>()
                },
                Page = Vns430Page.Status,
                PageGroup = Vns430PageGroup.Nav,
                CursorActive = true
            };

            using Bitmap raw = Vns430LcdRenderer.Render(state, applyAppearance: false);
            using Bitmap shaded = Vns430LcdRenderer.Render(state);

            Assert.Equal(raw.Width, shaded.Width);
            Assert.Equal(raw.Height, shaded.Height);

            // Bloom and softening must visibly change the raster, and must widen the
            // palette rather than collapse it, so strokes read heavier without the
            // display losing legible contrast.
            int changed = 0;
            HashSet<int> rawColors = new();
            HashSet<int> shadedColors = new();
            for (int y = 0; y < raw.Height; y++)
            {
                for (int x = 0; x < raw.Width; x++)
                {
                    int before = raw.GetPixel(x, y).ToArgb();
                    int after = shaded.GetPixel(x, y).ToArgb();
                    rawColors.Add(before);
                    shadedColors.Add(after);
                    if (before != after)
                    {
                        changed++;
                    }
                }
            }

            Assert.True(changed > 0, "The appearance pass left the raster untouched.");
            Assert.True(shadedColors.Count > rawColors.Count,
                "The appearance pass did not introduce intermediate shades.");
        }

        // Vns430Form's knob handling cannot be reached through a constructed form: that
        // needs a live MainForm backend and a window handle. RotateLarge and RotateSmall
        // only read and write fields, so an uninitialised instance with those fields set
        // exercises the real navigation logic without a UI.
        private static object BareForm(Vns430Page page, Vns430PageGroup group, bool cursorActive, int selectedIndex = 0)
        {
            object form = RuntimeHelpers.GetUninitializedObject(typeof(Vns430Form));
            SetField(form, "page", page);
            SetField(form, "pageGroup", group);
            SetField(form, "cursorActive", cursorActive);
            SetField(form, "selectedIndex", selectedIndex);
            SetField(form, "detailScrollLine", 0);
            return form;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = typeof(Vns430Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field != null, $"Vns430Form.{name} was renamed; update this test.");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)typeof(Vns430Form)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(target);
        }

        private static void Turn(object form, string method, int direction)
        {
            MethodInfo rotate = typeof(Vns430Form)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(rotate != null, $"Vns430Form.{method} was renamed; update this test.");
            rotate.Invoke(form, new object[] { direction });
        }

        private static List<Vns430Command> PanelButtonCommands()
        {
            object form = RuntimeHelpers.GetUninitializedObject(typeof(Vns430Form));
            MethodInfo create = typeof(Vns430Form)
                .GetMethod("CreatePanelButtons", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(create != null, "Vns430Form.CreatePanelButtons was renamed; update this test.");

            List<Vns430Command> commands = new();
            foreach (object button in (System.Collections.IEnumerable)create.Invoke(form, null))
            {
                PropertyInfo property = button.GetType()
                    .GetProperty("Command", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(property != null, "PanelButton.Command was renamed; update this test.");
                commands.Add((Vns430Command)property.GetValue(button));
            }

            return commands;
        }

        [Fact]
        public void PanelButtons_EachBindToADistinctCommand()
        {
            List<Vns430Command> commands = PanelButtonCommands();
            Assert.NotEmpty(commands);

            string[] duplicated = commands
                .GroupBy(command => command)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} ({(int)group.Key}) on {group.Count()} buttons")
                .ToArray();

            Assert.True(duplicated.Length == 0,
                "Panel buttons must each drive a different command, otherwise two faces do " +
                "the same job and a command has no button at all. Duplicated: " +
                string.Join("; ", duplicated));
        }

        [Fact]
        public void PanelButtons_CoverEveryNonEncoderCommand()
        {
            // 1-4 are the two encoder rings and 5 is the cursor push, so the face keys own
            // 6 through 17. Power (18) is deliberately excluded: it shows and hides the
            // window, which is worth a hardware key through the module L-var but not a
            // mouse target on a window that already has a close button. Anything else
            // missing is a command nobody can reach from the panel.
            List<Vns430Command> commands = PanelButtonCommands();
            List<Vns430Command> expected = Enum.GetValues(typeof(Vns430Command))
                .Cast<Vns430Command>()
                .Where(command => (int)command >= 6 && (int)command <= 17)
                .ToList();

            Vns430Command[] unreachable = expected.Except(commands).ToArray();
            Assert.True(unreachable.Length == 0,
                "No panel button sends: " + string.Join(", ", unreachable.Select(c => $"{c} ({(int)c})")));

            Assert.DoesNotContain(Vns430Command.Power, commands);
        }

        [Fact]
        public void UtilityPage_LargeKnobMovesTheSelection()
        {
            // The UTILITY page draws a selection bar from SelectedIndex, but the large
            // knob used to step detailScrollLine, which that page never reads, so the
            // bar could not be moved off the first line.
            object form = BareForm(Vns430Page.Help, Vns430PageGroup.Aux, cursorActive: true);

            Turn(form, "RotateLarge", 1);
            Assert.Equal(1, GetField<int>(form, "selectedIndex"));

            Turn(form, "RotateLarge", 1);
            Assert.Equal(2, GetField<int>(form, "selectedIndex"));

            Turn(form, "RotateLarge", -1);
            Assert.Equal(1, GetField<int>(form, "selectedIndex"));
        }

        [Fact]
        public void UtilityPage_SelectionWrapsWithinTheRenderedItems()
        {
            int last = Vns430LcdRenderer.HelpItems.Length - 1;
            object form = BareForm(Vns430Page.Help, Vns430PageGroup.Aux, cursorActive: true, selectedIndex: last);

            Turn(form, "RotateLarge", 1);
            Assert.Equal(0, GetField<int>(form, "selectedIndex"));

            Turn(form, "RotateLarge", -1);
            Assert.Equal(last, GetField<int>(form, "selectedIndex"));
        }

        // Ints rather than the enums themselves: the test method has to be public for
        // xunit to discover it, and Vns430Page/Vns430PageGroup are internal.
        [Theory]
        [InlineData((int)Vns430Page.AtcMenu, (int)Vns430PageGroup.Wpt)]
        [InlineData((int)Vns430Page.AocMenu, (int)Vns430PageGroup.Aux)]
        [InlineData((int)Vns430Page.Help, (int)Vns430PageGroup.Aux)]
        public void ListPages_SmallKnobStillChangesPage(int pageValue, int groupValue)
        {
            // These pages switch the cursor on as soon as they open, and the cursor used
            // to capture both knobs, so there was no way to page off them at all.
            Vns430Page page = (Vns430Page)pageValue;
            object form = BareForm(page, (Vns430PageGroup)groupValue, cursorActive: true);

            Turn(form, "RotateSmall", 1);

            Assert.True(GetField<Vns430Page>(form, "page") != page,
                $"The small knob did not page away from {page}.");
        }

        [Fact]
        public void MenuOverlay_SmallKnobDoesNotPageAway()
        {
            // Menu belongs to no page group, so paging from it would land the user on an
            // unrelated page. It stays captured by the cursor on purpose.
            object form = BareForm(Vns430Page.Menu, Vns430PageGroup.Nav, cursorActive: true);

            Turn(form, "RotateSmall", 1);

            Assert.Equal(Vns430Page.Menu, GetField<Vns430Page>(form, "page"));
        }

        private static Vns430LcdState FingerprintBaseline() => new()
        {
            Snapshot = new Vns430BackendSnapshot
            {
                Connected = true,
                Callsign = "N731CD",
                CurrentAtcUnit = "KZAK",
                PendingLogon = "KZAK",
                Departure = "KSFO",
                Arrival = "PHNL",
                Aircraft = "B738",
                Messages = new List<Vns430MessageSnapshot>
                {
                    new()
                    {
                        Type = "CPDLC",
                        Station = "KZAK",
                        Text = "CLIMB TO FL350",
                        Responses = new[] { "WILCO", "UNABLE" }
                    }
                }
            },
            Page = Vns430Page.Status,
            PageGroup = Vns430PageGroup.Nav,
            CursorActive = true,
            SelectedIndex = 0,
            DetailScrollLine = 0,
            ResponseIndex = 0,
            ZoomLevel = 1,
            LogonCode = "____",
            LogonCharacter = 0,
            TransientStatus = string.Empty,
            MenuItems = new[] { "ONE", "TWO" },
            Workflow = Vns430Workflow.Create(Vns430WorkflowKind.AtcLevel, new Vns430BackendSnapshot()),
            WorkflowCharacter = 0,
            LoadSession = new Vns430LoadControlSession { AircraftIndex = 0, CabinIndex = 0, FormatIndex = 0 },
            OperationBusy = false,
            OperationStatus = string.Empty
        };

        // One distinct value per rendered property. A property with no entry here is a
        // property nobody proved the fingerprint covers, so the test below fails until
        // someone decides which it is.
        private static readonly Dictionary<string, object> FingerprintVariants = new()
        {
            [nameof(Vns430LcdState.Snapshot)] = new Vns430BackendSnapshot { Callsign = "G-ABCD" },
            [nameof(Vns430LcdState.Page)] = Vns430Page.Help,
            [nameof(Vns430LcdState.PageGroup)] = Vns430PageGroup.Aux,
            [nameof(Vns430LcdState.CursorActive)] = false,
            [nameof(Vns430LcdState.SelectedIndex)] = 3,
            [nameof(Vns430LcdState.DetailScrollLine)] = 2,
            [nameof(Vns430LcdState.ResponseIndex)] = 1,
            [nameof(Vns430LcdState.ZoomLevel)] = 2,
            [nameof(Vns430LcdState.LogonCode)] = "AB12",
            [nameof(Vns430LcdState.LogonCharacter)] = 2,
            [nameof(Vns430LcdState.TransientStatus)] = "SENT",
            [nameof(Vns430LcdState.MessageFilter)] = "SENT",
            [nameof(Vns430LcdState.MenuItems)] = new[] { "ONE", "THREE" },
            [nameof(Vns430LcdState.Workflow)] =
                Vns430Workflow.Create(Vns430WorkflowKind.AocMetar, new Vns430BackendSnapshot()),
            [nameof(Vns430LcdState.WorkflowCharacter)] = 4,
            [nameof(Vns430LcdState.LoadSession)] =
                new Vns430LoadControlSession { AircraftIndex = 1, CabinIndex = 0, FormatIndex = 0 },
            [nameof(Vns430LcdState.OperationBusy)] = true,
            [nameof(Vns430LcdState.OperationStatus)] = "WORKING"
        };

        [Fact]
        public void LcdState_FingerprintCoversEveryRenderedProperty()
        {
            PropertyInfo[] properties = typeof(Vns430LcdState)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property => property.CanWrite)
                .ToArray();

            Assert.NotEmpty(properties);
            ulong baseline = FingerprintBaseline().Fingerprint();

            foreach (PropertyInfo property in properties)
            {
                Assert.True(
                    FingerprintVariants.ContainsKey(property.Name),
                    $"Vns430LcdState.{property.Name} is rendered but has no fingerprint coverage. " +
                    "Add it to Fingerprint() and give it a variant here, or the panel will " +
                    "show a stale display when only that property changes.");

                Vns430LcdState mutated = FingerprintBaseline();
                property.SetValue(mutated, FingerprintVariants[property.Name]);

                Assert.True(
                    mutated.Fingerprint() != baseline,
                    $"Changing Vns430LcdState.{property.Name} did not change the fingerprint, " +
                    "so the panel would keep showing the previous display.");
            }
        }

        [Fact]
        public void LcdState_FingerprintTracksValuesMutatedInPlace()
        {
            // The workflow object is reused while the pilot types into it, so identity
            // comparison would never notice the edit.
            Vns430LcdState state = FingerprintBaseline();
            ulong before = state.Fingerprint();

            state.Workflow.Fields[0].Value = "EGLL";

            Assert.True(state.Fingerprint() != before,
                "Editing a workflow field in place must change the fingerprint.");
        }

        [Fact]
        public void LcdState_FingerprintIsStableForUnchangedState()
        {
            Assert.Equal(FingerprintBaseline().Fingerprint(), FingerprintBaseline().Fingerprint());
        }

        [Fact]
        public void LcdRenderer_AppearancePassReusesItsScratchBuffers()
        {
            Vns430LcdState state = new()
            {
                Snapshot = new Vns430BackendSnapshot
                {
                    Connected = true,
                    Callsign = "N731CD",
                    CurrentAtcUnit = "KZAK",
                    Messages = new List<Vns430MessageSnapshot>()
                },
                Page = Vns430Page.Status,
                PageGroup = Vns430PageGroup.Nav,
                CursorActive = true
            };

            // The panel repaints on a 100 ms timer, so per-render scratch buffers turn
            // into megabytes of garbage per second. Measure the shaded render against
            // the raw one: the appearance pass must not widen the gap.
            //
            // GetAllocatedBytesForCurrentThread, not GetTotalAllocatedBytes: the latter
            // is process-wide, so test classes running on other threads land in the
            // measurement and make this flaky. The renderer's buffers are [ThreadStatic],
            // so per-thread accounting is also the correct scope.
            for (int i = 0; i < 8; i++)
            {
                using Bitmap warmRaw = Vns430LcdRenderer.Render(state, applyAppearance: false);
                using Bitmap warmShaded = Vns430LcdRenderer.Render(state);
            }

            const int runs = 16;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < runs; i++)
            {
                using Bitmap raw = Vns430LcdRenderer.Render(state, applyAppearance: false);
            }

            long rawBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < runs; i++)
            {
                using Bitmap shaded = Vns430LcdRenderer.Render(state);
            }

            long shadedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            long overheadPerRender = (shadedBytes - rawBytes) / runs;
            Assert.True(overheadPerRender < 4096,
                $"The appearance pass allocated {overheadPerRender} bytes per render; " +
                "its scratch buffers should be reused across renders.");
        }

        [Fact]
        public void PhotoDerivedPanelArtwork_EmbedsBackgroundAndMovementStates()
        {
            using Vns430PanelArtwork artwork = new();
            Assert.Equal(960, artwork.PanelBackground.Width);
            Assert.Equal(407, artwork.PanelBackground.Height);

            using Bitmap surface = new(960, 407);
            using Graphics graphics = Graphics.FromImage(surface);
            artwork.DrawState(graphics, "enter", "pressed");
            artwork.DrawState(graphics, "right_encoder", "large-cw");
            artwork.DrawState(graphics, "right_encoder", "small-ccw");
        }

        [Fact]
        public void RangeRocker_UsesTwoGradientSegmentsWithOneSharedJoin()
        {
            RectangleF control = Vns430PanelArtwork.ControlBounds("range");
            RectangleF decrease = Vns430PanelArtwork.PressedSegmentBounds("range", "decrease-pressed", control);
            RectangleF increase = Vns430PanelArtwork.PressedSegmentBounds("range", "increase-pressed", control);

            Assert.Equal(decrease.Right, increase.Left);
            Assert.True(decrease.Left > control.Left);
            Assert.True(increase.Right < control.Right);
            Assert.Equal(decrease.Top, increase.Top);
            Assert.Equal(decrease.Height, increase.Height);
        }

        [Theory]
        [InlineData((int)Vns430PageGroup.Nav, "DLK")]
        [InlineData((int)Vns430PageGroup.Wpt, "ATC")]
        [InlineData((int)Vns430PageGroup.Aux, "AOC")]
        [InlineData((int)Vns430PageGroup.Nrst, "MSG")]
        public void PageGroupLabels_DescribeTheDatalinkFunctions(int group, string expected)
        {
            Assert.Equal(expected, Vns430LcdRenderer.PageGroupLabel((Vns430PageGroup)group));
        }

        [Fact]
        public void ComplexAtcWorkflow_BuildsTheBackendPacketTextFromRotaryFields()
        {
            Vns430BackendSnapshot snapshot = new() { CurrentAtcUnit = "KZAK" };
            Vns430Workflow workflow = Vns430Workflow.Create(Vns430WorkflowKind.AtcDirect, snapshot);
            workflow.Fields.Single(field => field.Key == "VALUE").Value = "DAG";
            workflow.Fields.Single(field => field.Key == "DUE").Value = "WX";
            workflow.Fields.Single(field => field.Key == "REMARKS").Value = "RIDE";

            Assert.Equal(string.Empty, workflow.ValidationError());
            Assert.Equal("REQUEST DIRECT TO DAG DUE TO WEATHER RIDE", workflow.BuildMessage(snapshot));
        }

        [Fact]
        public void PositionReportWorkflow_BuildsThePposPacketAndOmitsBlankTail()
        {
            Vns430BackendSnapshot snapshot = new() { CurrentAtcUnit = "EDYY" };
            Vns430Workflow workflow = Vns430Workflow.Create(Vns430WorkflowKind.AtcPositionReport, snapshot);

            // RECIPIENT prefilled from the current ATS unit; TIME prefilled with UTC HHmm.
            Assert.Equal("EDYY", workflow.Fields.Single(field => field.Key == "RECIPIENT").CleanValue);
            Assert.Equal(4, workflow.Fields.Single(field => field.Key == "TIME").CleanValue.Length);

            workflow.Fields.Single(field => field.Key == "FIX").Value = "ABNIL";
            workflow.Fields.Single(field => field.Key == "TIME").Value = "1830";
            workflow.Fields.Single(field => field.Key == "FL").Value = "350";
            workflow.Fields.Single(field => field.Key == "NEXT").Value = "RASVO";

            // With ETA and THEN left blank, the optional tail is omitted rather than padded.
            Assert.Equal(string.Empty, workflow.ValidationError());
            Assert.Equal("POSITION REPORT PPOS ABNIL AT 1830Z FL350 TO RASVO", workflow.BuildMessage(snapshot));

            workflow.Fields.Single(field => field.Key == "ETA").Value = "1845";
            workflow.Fields.Single(field => field.Key == "THEN").Value = "DEXIT";
            Assert.Equal("POSITION REPORT PPOS ABNIL AT 1830Z FL350 TO RASVO AT 1845Z NEXT DEXIT",
                workflow.BuildMessage(snapshot));

            // TIME/FL are length-checked.
            workflow.Fields.Single(field => field.Key == "FL").Value = "35";
            Assert.Equal("CHECK TIME/FL", workflow.ValidationError());
        }

        [Fact]
        public void AocAndLoadPages_RenderNativelyWithoutOpeningLegacyForms()
        {
            Vns430BackendSnapshot snapshot = new()
            {
                Connected = true,
                Callsign = "DAL123",
                Departure = "KSEA",
                Arrival = "KSFO",
                Aircraft = "B738"
            };
            foreach (Vns430Page page in new[]
            {
                Vns430Page.AtcMenu, Vns430Page.AtcRequest, Vns430Page.RequestReview,
                Vns430Page.AocMenu, Vns430Page.AocRequest, Vns430Page.AocReview,
                Vns430Page.LoadControl, Vns430Page.LoadReview
            })
            {
                Vns430Workflow workflow = page is Vns430Page.AtcRequest or Vns430Page.RequestReview
                    ? Vns430Workflow.Create(Vns430WorkflowKind.AtcLevel, snapshot)
                    : Vns430Workflow.Create(Vns430WorkflowKind.AocTelex, snapshot);
                using Bitmap image = Vns430LcdRenderer.Render(new Vns430LcdState
                {
                    Snapshot = snapshot,
                    Page = page,
                    PageGroup = page is Vns430Page.AtcMenu or Vns430Page.AtcRequest or Vns430Page.RequestReview
                        ? Vns430PageGroup.Wpt
                        : Vns430PageGroup.Aux,
                    Workflow = workflow,
                    CursorActive = true,
                    OperationStatus = "ENT LOAD SIMBRIEF"
                });
                Assert.Equal(240, image.Width);
                Assert.Equal(128, image.Height);
            }
        }

        [Theory]
        [InlineData("pressed")]
        [InlineData("decrease-pressed")]
        [InlineData("increase-pressed")]
        public void ButtonPresses_ReuseTheRegisteredNormalPhotoCrop(string state)
        {
            Assert.Equal("normal", Vns430PanelArtwork.AssetStateFor(state));
        }

        [Theory]
        [InlineData(0, 0, (int)Vns430Command.CursorPush)]
        [InlineData(-38, 0, (int)Vns430Command.None)]
        [InlineData(38, 0, (int)Vns430Command.None)]
        [InlineData(-63, 0, (int)Vns430Command.None)]
        [InlineData(63, 0, (int)Vns430Command.None)]
        [InlineData(80, 0, (int)Vns430Command.None)]
        public void LeftClick_OnlyPushesTheEncoderCenter(float offsetX, float offsetY, int expected)
        {
            PointF center = new(878, 326);
            PointF point = new(center.X + offsetX, center.Y + offsetY);

            Assert.Equal((Vns430Command)expected, Vns430Form.KnobPushCommandAt(point, center));
        }

        [Theory]
        [InlineData(0, 0, 120, (int)Vns430Command.SmallRightIncrease)]
        [InlineData(-38, 0, -120, (int)Vns430Command.SmallRightDecrease)]
        [InlineData(38, 0, 120, (int)Vns430Command.SmallRightIncrease)]
        [InlineData(-63, 0, -120, (int)Vns430Command.LargeRightDecrease)]
        [InlineData(63, 0, 120, (int)Vns430Command.LargeRightIncrease)]
        [InlineData(80, 0, 120, (int)Vns430Command.None)]
        [InlineData(38, 0, 0, (int)Vns430Command.None)]
        public void MouseWheel_RotatesTheRingUnderThePointer(float offsetX, float offsetY, int delta, int expected)
        {
            PointF center = new(878, 326);
            PointF point = new(center.X + offsetX, center.Y + offsetY);

            Assert.Equal((Vns430Command)expected, Vns430Form.KnobWheelCommandAt(point, center, delta));
        }

        [Fact]
        public void GnsPanel_DoesNotOverrideKeyboardCommandHandling()
        {
            Assert.Null(typeof(Vns430Form).GetMethod(
                "ProcessCmdKey",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
        }

        [Fact]
        public void EncoderSprites_MoveInnerAndOuterRingsIndependently()
        {
            using Vns430PanelArtwork artwork = new();
            Rectangle bounds = Rectangle.Round(Vns430PanelArtwork.ControlBounds("right_encoder"));
            using Bitmap normal = RenderPanelState(artwork, "right_encoder", "normal");
            using Bitmap small = RenderPanelState(artwork, "right_encoder", "small-cw");
            using Bitmap large = RenderPanelState(artwork, "right_encoder", "large-cw");

            PointF center = new(bounds.Left + (bounds.Width / 2f), bounds.Top + (bounds.Height / 2f));
            int smallInnerChanges = CountChangedPixels(normal, small, center, 0, 43);
            int smallOuterChanges = CountChangedPixels(normal, small, center, 52, 75);
            int largeInnerChanges = CountChangedPixels(normal, large, center, 0, 43);
            int largeOuterChanges = CountChangedPixels(normal, large, center, 52, 75);

            Assert.True(smallInnerChanges > smallOuterChanges, $"Small knob changed inner={smallInnerChanges}, outer={smallOuterChanges} pixels.");
            Assert.True(largeOuterChanges > largeInnerChanges, $"Large knob changed outer={largeOuterChanges}, inner={largeInnerChanges} pixels.");
        }

        [Theory]
        [InlineData("left_encoder", 78, 82)]
        [InlineData("right_encoder", 79, 83)]
        public void EncoderArtwork_UsesCalibratedMechanicalPivots(string control, float expectedX, float expectedY)
        {
            PointF pivot = Vns430PanelArtwork.EncoderPivot(control);

            Assert.Equal(expectedX, pivot.X);
            Assert.Equal(expectedY, pivot.Y);
        }

        private static Bitmap RenderPanelState(Vns430PanelArtwork artwork, string control, string state)
        {
            Bitmap surface = new(960, 407);
            using Graphics graphics = Graphics.FromImage(surface);
            graphics.DrawImageUnscaled(artwork.PanelBackground, 0, 0);
            artwork.DrawState(graphics, control, state);
            return surface;
        }

        private static int CountChangedPixels(Bitmap first, Bitmap second, PointF center, float minimumRadius, float maximumRadius)
        {
            int count = 0;
            int left = Math.Max(0, (int)(center.X - maximumRadius));
            int right = Math.Min(first.Width - 1, (int)(center.X + maximumRadius));
            int top = Math.Max(0, (int)(center.Y - maximumRadius));
            int bottom = Math.Min(first.Height - 1, (int)(center.Y + maximumRadius));
            float minimumSquared = minimumRadius * minimumRadius;
            float maximumSquared = maximumRadius * maximumRadius;

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    float dx = x - center.X;
                    float dy = y - center.Y;
                    float distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared >= minimumSquared && distanceSquared <= maximumSquared && first.GetPixel(x, y) != second.GetPixel(x, y))
                    {
                        count += 1;
                    }
                }
            }

            return count;
        }
    }
}
