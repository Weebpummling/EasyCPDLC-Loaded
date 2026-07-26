using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyCPDLC.VNS430
{
    internal sealed class Vns430EditField
    {
        internal string Key { get; init; } = string.Empty;
        internal string Label { get; init; } = string.Empty;
        internal string Value { get; set; } = string.Empty;
        internal int MaxLength { get; init; } = 8;
        internal bool Required { get; init; }
        internal IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

        internal bool IsOption => Options.Count > 0;

        internal void Step(int character, int direction)
        {
            if (IsOption)
            {
                int current = Options.ToList().FindIndex(option =>
                    string.Equals(option, Value, StringComparison.OrdinalIgnoreCase));
                Value = Options[Vns430Form.Wrap(current + direction, Options.Count)];
                return;
            }

            const string characters = "_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ./-";
            char[] value = (Value ?? string.Empty).PadRight(MaxLength, '_').Substring(0, MaxLength).ToCharArray();
            int position = Math.Clamp(character, 0, Math.Max(0, MaxLength - 1));
            int index = characters.IndexOf(value[position]);
            value[position] = characters[Vns430Form.Wrap(index + direction, characters.Length)];
            Value = new string(value).TrimEnd('_');
        }

        internal void ClearCharacter(int character)
        {
            if (IsOption)
            {
                Value = Options.FirstOrDefault() ?? string.Empty;
                return;
            }

            char[] value = (Value ?? string.Empty).PadRight(MaxLength, '_').Substring(0, MaxLength).ToCharArray();
            value[Math.Clamp(character, 0, Math.Max(0, MaxLength - 1))] = '_';
            Value = new string(value).TrimEnd('_');
        }

        internal string CleanValue => (Value ?? string.Empty).Replace('_', ' ').Trim().ToUpperInvariant();
    }

    internal sealed class Vns430Workflow
    {
        internal Vns430WorkflowKind Kind { get; init; }
        internal string Title { get; init; } = string.Empty;
        internal List<Vns430EditField> Fields { get; init; } = new();

        internal string Value(string key) => Fields.FirstOrDefault(field => field.Key == key)?.CleanValue ?? string.Empty;

        internal string ValidationError()
        {
            Vns430EditField missing = Fields.FirstOrDefault(field => field.Required && string.IsNullOrWhiteSpace(field.CleanValue));
            if (missing != null)
            {
                return missing.Label + " REQUIRED";
            }

            string recipient = Value("RECIPIENT");
            if (Fields.Any(field => field.Key == "RECIPIENT") && recipient.Length < 3)
            {
                return "RECIPIENT TOO SHORT";
            }

            if (Kind == Vns430WorkflowKind.AocMetar && Value("STATION").Length != 4)
            {
                return "ENTER 4-CHAR ICAO";
            }

            if (Kind == Vns430WorkflowKind.AocAtis && Value("STATION").Length != 4)
            {
                return "ENTER 4-CHAR ICAO";
            }

            if (Kind == Vns430WorkflowKind.AocPreDeparture && Value("ATIS").Length != 1)
            {
                return "ATIS LETTER REQUIRED";
            }

            if (Kind == Vns430WorkflowKind.AocOceanic &&
                (Value("ETA").Length != 4 || Value("MACH").Length != 2 || Value("LEVEL").Length != 3))
            {
                return "CHECK ETA/MACH/LEVEL";
            }

            if (Kind == Vns430WorkflowKind.AtcPositionReport &&
                (Value("TIME").Length != 4 || Value("FL").Length != 3))
            {
                return "CHECK TIME/FL";
            }

            string whenType = Value("TYPE");
            if (Kind == Vns430WorkflowKind.AtcWhenCanWe &&
                new[] { "CLIMB", "DESCENT", "MACH", "SPEED", "DIRECT" }.Contains(whenType) &&
                string.IsNullOrWhiteSpace(Value("VALUE")))
            {
                return "REQUEST VALUE REQUIRED";
            }

            return string.Empty;
        }

        internal string BuildMessage(Vns430BackendSnapshot snapshot)
        {
            string due = Value("DUE") switch
            {
                "WX" => " DUE TO WEATHER",
                "A/C" => " DUE TO PERFORMANCE",
                _ => string.Empty
            };
            string remarks = Value("REMARKS");
            string suffix = due + (string.IsNullOrWhiteSpace(remarks) ? string.Empty : " " + remarks);

            return Kind switch
            {
                Vns430WorkflowKind.AtcDirect => "REQUEST DIRECT TO " + Value("VALUE") + suffix,
                Vns430WorkflowKind.AtcLevel => "REQUEST FL" + Value("VALUE") + suffix,
                Vns430WorkflowKind.AtcSpeed => "REQUEST " +
                    (Value("SPEED TYPE") == "MACH" ? "M" + Value("VALUE") : Value("VALUE") + "K") + suffix,
                Vns430WorkflowKind.AtcWhenCanWe => BuildWhenCanWe(Value("TYPE"), Value("VALUE"), remarks),
                Vns430WorkflowKind.AtcFreeText => Value("TEXT"),
                Vns430WorkflowKind.AtcPositionReport =>
                    "POSITION REPORT PPOS " + Value("FIX") + " AT " + Value("TIME") + "Z FL" + Value("FL") +
                    " TO " + Value("NEXT") +
                    (string.IsNullOrWhiteSpace(Value("ETA")) ? string.Empty : " AT " + Value("ETA") + "Z") +
                    (string.IsNullOrWhiteSpace(Value("THEN")) ? string.Empty : " NEXT " + Value("THEN")),
                Vns430WorkflowKind.AocTelex => Value("TEXT"),
                Vns430WorkflowKind.AocPreDeparture =>
                    "REQUEST PREDEP CLEARANCE " + snapshot.Callsign + " " + snapshot.Aircraft +
                    " TO " + snapshot.Arrival + " AT " + snapshot.Departure + " STAND " + Value("GATE") +
                    " ATIS " + Value("ATIS") + (string.IsNullOrWhiteSpace(remarks) ? string.Empty : " " + remarks),
                Vns430WorkflowKind.AocOceanic =>
                    "OCEANIC CLEARANCE REQUEST CALLSIGN " + snapshot.Callsign + " ENTRY POINT " + Value("ENTRY") +
                    " AT " + Value("ETA") + " REQ M" + Value("MACH") + " FL" + Value("LEVEL") +
                    (string.IsNullOrWhiteSpace(remarks) ? string.Empty : " " + remarks),
                Vns430WorkflowKind.AocMetar => "METAR " + Value("STATION"),
                Vns430WorkflowKind.AocAtis => "ATIS " + Value("STATION") + " " + Value("TYPE"),
                _ => string.Empty
            };
        }

        private static string BuildWhenCanWe(string type, string value, string remarks)
        {
            string request = type switch
            {
                "HIGHER" => "WHEN CAN WE EXPECT HIGHER LEVEL",
                "LOWER" => "WHEN CAN WE EXPECT LOWER LEVEL",
                "BACK ROUTE" => "WHEN CAN WE EXPECT BACK ON ROUTE",
                "CLIMB" => "WHEN CAN WE EXPECT CLIMB TO FL" + value,
                "DESCENT" => "WHEN CAN WE EXPECT DESCENT TO FL" + value,
                "MACH" => "WHEN CAN WE EXPECT M" + value,
                "SPEED" => "WHEN CAN WE EXPECT " + value + "K",
                "DIRECT" => "WHEN CAN WE EXPECT DIRECT TO " + value,
                _ => "WHEN CAN WE EXPECT " + value
            };
            return request + (string.IsNullOrWhiteSpace(remarks) ? string.Empty : " " + remarks);
        }

        internal static Vns430Workflow Create(Vns430WorkflowKind kind, Vns430BackendSnapshot snapshot)
        {
            string recipient = !string.IsNullOrWhiteSpace(snapshot.CurrentAtcUnit)
                ? snapshot.CurrentAtcUnit
                : snapshot.PendingLogon;
            // METAR/ATIS prefill: the departure airport until the flight reaches cruise,
            // the destination from then on - matching what the pilot actually needs at
            // each point. The other airport is one field-edit away.
            string station = snapshot.PreferArrivalStation
                ? (!string.IsNullOrWhiteSpace(snapshot.Arrival) ? snapshot.Arrival : snapshot.Departure)
                : (!string.IsNullOrWhiteSpace(snapshot.Departure) ? snapshot.Departure : snapshot.Arrival);
            Vns430EditField Text(string key, string label, int length, bool required = false, string value = "") =>
                new() { Key = key, Label = label, MaxLength = length, Required = required, Value = value };
            Vns430EditField Options(string key, string label, params string[] values) =>
                new() { Key = key, Label = label, Options = values, Value = values.FirstOrDefault() ?? string.Empty };

            // Every AOC page carries a VIA selector so the pilot picks the network per
            // request instead of from a global setting. The instruments pin it to the
            // bottom of the page, whatever order the fields are declared in.
            //
            // Datalink pages choose an ACARS network; the default follows the side the
            // pilot is flying on, with VA traffic normally living on Hoppie.
            Vns430EditField DatalinkVia() => Options("VIA", "SEND VIA",
                snapshot.SayIntentionsNetwork ? new[] { "SI", "HOPPIE" } : new[] { "HOPPIE", "SI" });

            // Weather pages choose a source instead: VATSIM goes out as a Hoppie INFOREQ,
            // REAL WORLD and SI are fetched directly over HTTP. The current source leads
            // so the selector opens where the last request left it.
            Vns430EditField WeatherVia()
            {
                string[] sources = new[] { "VATSIM", "REAL WORLD", "SI" }
                    .OrderBy(source => string.Equals(source, snapshot.WeatherSource, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToArray();
                return Options("VIA", "REQUEST VIA", sources);
            }

            return kind switch
            {
                Vns430WorkflowKind.AtcDirect => new() { Kind = kind, Title = "DIRECT REQUEST", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("VALUE", "WAYPOINT", 8, true), Options("DUE", "DUE TO", "NONE", "WX", "A/C"), Text("REMARKS", "REMARKS", 48) } },
                Vns430WorkflowKind.AtcLevel => new() { Kind = kind, Title = "LEVEL REQUEST", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("VALUE", "FL", 3, true), Options("DUE", "DUE TO", "NONE", "WX", "A/C"), Text("REMARKS", "REMARKS", 48) } },
                Vns430WorkflowKind.AtcSpeed => new() { Kind = kind, Title = "SPEED REQUEST", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Options("SPEED TYPE", "FORMAT", "MACH", "KNOTS"), Text("VALUE", "SPEED", 3, true), Options("DUE", "DUE TO", "NONE", "WX", "A/C"), Text("REMARKS", "REMARKS", 48) } },
                Vns430WorkflowKind.AtcWhenCanWe => new() { Kind = kind, Title = "WHEN CAN WE", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Options("TYPE", "REQUEST", "HIGHER", "LOWER", "BACK ROUTE", "CLIMB", "DESCENT", "MACH", "SPEED", "DIRECT"), Text("VALUE", "VALUE", 8), Text("REMARKS", "REMARKS", 48) } },
                Vns430WorkflowKind.AtcFreeText => new() { Kind = kind, Title = "ATC FREE TEXT", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("TEXT", "MESSAGE", 80, true) } },
                Vns430WorkflowKind.AtcPositionReport => new() { Kind = kind, Title = "POSITION REPORT", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("FIX", "PPOS FIX", 7, true), Text("TIME", "TIME Z", 4, true, DateTime.UtcNow.ToString("HHmm")), Text("FL", "FL", 3, true), Text("NEXT", "NEXT FIX", 7, true), Text("ETA", "NEXT ETA", 4), Text("THEN", "THEN FIX", 7) } },
                Vns430WorkflowKind.AocTelex => new() { Kind = kind, Title = "AOC TELEX", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true), Text("TEXT", "MESSAGE", 80, true), DatalinkVia() } },
                Vns430WorkflowKind.AocMetar => new() { Kind = kind, Title = "METAR REQUEST", Fields = { Text("STATION", "STATION", 4, true, station), WeatherVia() } },
                // The ATIS TYPE default follows the same phase logic as the station
                // prefill (the first option is the default).
                Vns430WorkflowKind.AocAtis => new() { Kind = kind, Title = "ATIS REQUEST", Fields = { Text("STATION", "STATION", 4, true, station), Options("TYPE", "ATIS TYPE", snapshot.PreferArrivalStation ? new[] { "ARRIVAL", "DEPARTURE" } : new[] { "DEPARTURE", "ARRIVAL" }), Options("AUTO", "AUTO REFRESH", "OFF", "ON"), WeatherVia() } },
                Vns430WorkflowKind.AocPreDeparture => new() { Kind = kind, Title = "PREDEP CLEARANCE", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("GATE", "STAND/GATE", 5, true), Text("ATIS", "ATIS", 1, true), Text("REMARKS", "REMARKS", 40) } },
                Vns430WorkflowKind.AocOceanic => new() { Kind = kind, Title = "OCEANIC CLEARANCE", Fields = { Text("RECIPIENT", "RECIPIENT", 8, true, recipient), Text("ENTRY", "ENTRY POINT", 8, true), Text("ETA", "ENTRY ETA", 4, true), Text("MACH", "MACH", 2, true), Text("LEVEL", "FL", 3, true), Text("REMARKS", "REMARKS", 40), DatalinkVia() } },
                _ => new() { Kind = kind, Title = "REQUEST" }
            };
        }
    }

    internal sealed class Vns430OperationResult
    {
        internal bool Success { get; init; }
        internal string Status { get; init; } = string.Empty;
    }

    internal sealed class Vns430LoadControlSession
    {
        internal SimbriefLoadsheetData Flight { get; init; }
        internal ELoadReferenceData Reference { get; init; }
        internal int AircraftIndex { get; set; }
        internal int CabinIndex { get; set; }
        internal int FormatIndex { get; set; }
        internal List<PassengerClassAllocation> PassengerSplit { get; private set; } = new();

        // Simulated ground-crew loading time: how long after GENERATE the finished
        // loadsheet is delivered to the inbox.
        internal static readonly int[] LoadingTimeMinutes = { 0, 5, 15, 30 };
        internal int LoadingTimeIndex { get; set; }
        internal int LoadingMinutes => LoadingTimeMinutes[Math.Clamp(LoadingTimeIndex, 0, LoadingTimeMinutes.Length - 1)];
        internal string LoadingTimeLabel => LoadingMinutes <= 0 ? "INSTANT" : LoadingMinutes + " MIN";

        internal ELoadAircraft Aircraft => Reference.Aircraft[Math.Clamp(AircraftIndex, 0, Reference.Aircraft.Count - 1)];
        internal string Cabin => Aircraft.CabinConfigurations[Math.Clamp(CabinIndex, 0, Aircraft.CabinConfigurations.Count - 1)];
        internal ELoadFormat Format => Reference.Formats[Math.Clamp(FormatIndex, 0, Reference.Formats.Count - 1)];
        internal int FieldCount => 4 + PassengerSplit.Count;

        internal void RebuildPassengerSplit()
        {
            PassengerSplit = ELoadPassengerSplitter.Split(Cabin, Flight.PassengerCount)
                .Select(item => new PassengerClassAllocation { Code = item.Code, Capacity = item.Capacity, Passengers = item.Passengers })
                .ToList();
        }
    }
}
