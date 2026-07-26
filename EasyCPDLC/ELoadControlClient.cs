using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EasyCPDLC
{
    internal sealed class ELoadControlException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public ELoadControlException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal sealed class ELoadAircraft
    {
        public string Icao { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public IReadOnlyList<string> CabinConfigurations { get; init; } = Array.Empty<string>();
    }

    internal sealed class ELoadFormat
    {
        public string Name { get; init; } = string.Empty;
        public string TemplateId { get; init; } = string.Empty;
    }

    internal sealed class ELoadReferenceData
    {
        public IReadOnlyList<ELoadAircraft> Aircraft { get; init; } = Array.Empty<ELoadAircraft>();
        public IReadOnlyList<ELoadFormat> Formats { get; init; } = Array.Empty<ELoadFormat>();
    }

    internal sealed class ELoadLoadsheetResult
    {
        public string Loadsheet { get; init; } = string.Empty;
        public string AcarsMessage { get; init; } = string.Empty;
        public string LoadPlanning { get; init; } = string.Empty;
        public int EditionNumber { get; init; }
    }

    /// <summary>
    /// Guarantees a generated loadsheet states its weight units.
    /// </summary>
    /// <remarks>
    /// SimbriefLoadsheetData.Parse converts every weight to kilograms (SimBrief plans
    /// filed in pounds are multiplied by 0.45359237), and BuildGenerateRequest then
    /// sends bare numbers with no unit field - so the returned sheet is always in kg
    /// regardless of the pilot's SimBrief setting. A pilot planning in pounds would
    /// otherwise read kilogram figures with nothing on the sheet saying so.
    /// </remarks>
    internal static class ELoadLoadsheetUnits
    {
        internal const string UnitsLine = "ALL WEIGHTS IN KG";

        private static readonly Regex UnitsMentioned = new(
            @"\b(KGS?|KILOS?|KILOGRAMS?|LBS?|POUNDS?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static bool StatesUnits(string body) =>
            !string.IsNullOrWhiteSpace(body) && UnitsMentioned.IsMatch(body);

        /// <summary>Appends the units line only when the sheet does not state units.</summary>
        internal static string EnsureUnitsLine(string body)
        {
            string text = (body ?? string.Empty).TrimEnd();
            if (text.Length == 0 || StatesUnits(text))
            {
                return text;
            }

            return text + "\n\n" + UnitsLine;
        }
    }

    internal sealed class PassengerClassAllocation
    {
        public string Code { get; init; } = string.Empty;
        public int Capacity { get; init; }
        public int Passengers { get; set; }

        public string ApiField => "pax" + Code;
    }

    internal static class ELoadPassengerSplitter
    {
        private static readonly Regex CabinClassPattern = new(
            @"(?<class>F|C|J|W|Y|O|G|X|P|Z)(?<capacity>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<PassengerClassAllocation> Split(string cabinConfiguration, int totalPassengers)
        {
            int total = Math.Max(0, totalPassengers);
            List<PassengerClassAllocation> classes = CabinClassPattern.Matches(cabinConfiguration ?? string.Empty)
                .Cast<Match>()
                .Select(match => new PassengerClassAllocation
                {
                    Code = match.Groups["class"].Value.ToUpperInvariant(),
                    Capacity = int.Parse(match.Groups["capacity"].Value, CultureInfo.InvariantCulture)
                })
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PassengerClassAllocation
                {
                    Code = group.Key,
                    Capacity = group.Sum(item => item.Capacity)
                })
                .ToList();

            if (classes.Count == 0)
            {
                classes.Add(new PassengerClassAllocation { Code = "Y", Capacity = Math.Max(1, total) });
            }

            if (classes.Count == 1)
            {
                classes[0].Passengers = total;
                return classes;
            }

            int totalCapacity = Math.Max(1, classes.Sum(item => Math.Max(0, item.Capacity)));
            List<(PassengerClassAllocation Item, double Fraction)> remainders = new();
            int assigned = 0;

            foreach (PassengerClassAllocation item in classes)
            {
                double exact = total * (Math.Max(0, item.Capacity) / (double)totalCapacity);
                item.Passengers = Math.Min(item.Capacity, (int)Math.Floor(exact));
                assigned += item.Passengers;
                remainders.Add((item, exact - Math.Floor(exact)));
            }

            foreach ((PassengerClassAllocation item, _) in remainders
                .OrderByDescending(value => value.Fraction)
                .ThenByDescending(value => value.Item.Capacity))
            {
                if (assigned >= total)
                {
                    break;
                }

                int available = Math.Max(0, item.Capacity - item.Passengers);
                int add = Math.Min(available, total - assigned);
                item.Passengers += add;
                assigned += add;
            }

            // SimBrief can occasionally contain more passengers than the selected
            // cabin configuration. Preserve the total and let the confirmation UI
            // make the capacity issue visible instead of silently dropping people.
            if (assigned < total)
            {
                classes[^1].Passengers += total - assigned;
            }

            return classes;
        }
    }

    internal sealed class SimbriefLoadsheetData
    {
        public string Airline { get; init; } = string.Empty;
        public string FlightNumber { get; init; } = string.Empty;
        public string Departure { get; init; } = string.Empty;
        public string Destination { get; init; } = string.Empty;
        public string AircraftIcao { get; init; } = string.Empty;
        public string AircraftRegistration { get; init; } = string.Empty;
        public int BowKg { get; init; }
        public int CargoKg { get; init; }
        public int PassengerCount { get; init; }
        public int PassengerWeightKg { get; init; } = 86;
        public string CrewAmount { get; init; } = "02/04";
        public int BlockFuelKg { get; init; }
        public int TaxiFuelKg { get; init; }
        public int TakeoffFuelKg { get; init; }
        public int TripFuelKg { get; init; }
        public string TripTime { get; init; } = "0000";
        public string TaxiTime { get; init; } = "0000";
        public string FlightDate { get; init; } = string.Empty;
        public string DepartureTime { get; init; } = string.Empty;

        public string FlightKey => string.Join("-", Airline, FlightNumber, Departure, Destination, AircraftRegistration);

        public JObject BuildGenerateRequest(
            string cabinConfiguration,
            string formatId,
            IEnumerable<PassengerClassAllocation> passengerSplit,
            int editionNumber,
            string aircraftIcaoOverride = null)
        {
            JObject request = new()
            {
                ["airline"] = Airline,
                ["flightNumber"] = FlightNumber,
                ["departure"] = Departure,
                ["destination"] = Destination,
                ["type"] = string.IsNullOrWhiteSpace(aircraftIcaoOverride) ? AircraftIcao : aircraftIcaoOverride.Trim().ToUpperInvariant(),
                ["aircraftReg"] = AircraftRegistration,
                ["cabinConfig"] = cabinConfiguration,
                ["loadsheetFormat"] = formatId,
                ["BOW"] = BowKg,
                ["cargo"] = CargoKg,
                ["paxWeight"] = PassengerWeightKg,
                ["crewAmount"] = CrewAmount,
                ["blockFuel"] = BlockFuelKg,
                ["taxiFuel"] = TaxiFuelKg,
                ["toFuel"] = TakeoffFuelKg,
                ["tripFuel"] = TripFuelKg,
                ["tripTime"] = TripTime,
                ["taxiTime"] = TaxiTime,
                ["date"] = FlightDate,
                ["departureTime"] = DepartureTime,
                ["editionNumber"] = Math.Max(1, editionNumber)
            };

            foreach (PassengerClassAllocation item in passengerSplit ?? Array.Empty<PassengerClassAllocation>())
            {
                request[item.ApiField] = Math.Max(0, item.Passengers);
            }

            return request;
        }

        public static SimbriefLoadsheetData Parse(string json, string fallbackCallsign = "")
        {
            JObject root;
            try
            {
                root = JObject.Parse(json ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("SIMBRIEF RETURNED INVALID JSON.", ex);
            }

            string units = Text(root, "params.units", "params.units_weights").ToUpperInvariant();
            double factor = units.Contains("LB") ? 0.45359237 : 1.0;
            string airline = Text(root, "general.icao_airline", "general.airline").ToUpperInvariant();
            string callsign = Text(root, "atc.callsign", "general.callsign", "general.flight_number").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(airline))
            {
                airline = new string((fallbackCallsign ?? callsign).TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            }

            string flightNumber = Text(root, "general.flight_number", "atc.flight_number").ToUpperInvariant();
            if (flightNumber.StartsWith(airline, StringComparison.OrdinalIgnoreCase))
            {
                flightNumber = flightNumber.Substring(airline.Length);
            }
            if (string.IsNullOrWhiteSpace(flightNumber) && !string.IsNullOrWhiteSpace(callsign))
            {
                flightNumber = callsign.StartsWith(airline, StringComparison.OrdinalIgnoreCase)
                    ? callsign.Substring(airline.Length)
                    : callsign;
            }

            long scheduledOut = Long(root, "times.sched_out", "times.scheduled_out");
            DateTime departureUtc = scheduledOut > 100000000
                ? DateTimeOffset.FromUnixTimeSeconds(scheduledOut).UtcDateTime
                : DateTime.UtcNow;

            int pilots = Math.Max(1, Integer(root, "crew.pilots", "crew.flight_deck", "crew.flightdeck"));
            int cabinCrew = Math.Max(0, Integer(root, "crew.cabin", "crew.cabin_crew", "crew.cabincrew"));
            if (pilots == 1 && cabinCrew == 0)
            {
                pilots = 2;
                cabinCrew = 4;
            }

            SimbriefLoadsheetData data = new()
            {
                Airline = airline,
                FlightNumber = flightNumber,
                Departure = Text(root, "origin.icao_code", "origin.icao").ToUpperInvariant(),
                Destination = Text(root, "destination.icao_code", "destination.icao").ToUpperInvariant(),
                AircraftIcao = Text(root, "aircraft.icaocode", "aircraft.icao_code", "aircraft.icao").ToUpperInvariant(),
                AircraftRegistration = Text(root, "aircraft.reg", "aircraft.registration").ToUpperInvariant(),
                BowKg = WeightKg(root, factor, "weights.oew", "weights.bow"),
                CargoKg = WeightKg(root, factor, "weights.cargo"),
                PassengerCount = Math.Max(0, Integer(root, "weights.pax_count", "weights.passengers")),
                PassengerWeightKg = WeightKg(root, factor, "params.pax_weight", "weights.pax_weight_per_person") is int paxWeight && paxWeight > 0
                    ? paxWeight
                    : 86,
                CrewAmount = pilots.ToString("00", CultureInfo.InvariantCulture) + "/" + cabinCrew.ToString("00", CultureInfo.InvariantCulture),
                BlockFuelKg = WeightKg(root, factor, "fuel.plan_ramp", "fuel.block"),
                TaxiFuelKg = WeightKg(root, factor, "fuel.taxi"),
                TakeoffFuelKg = WeightKg(root, factor, "fuel.plan_takeoff", "fuel.takeoff"),
                TripFuelKg = WeightKg(root, factor, "fuel.enroute_burn", "fuel.trip"),
                TripTime = Duration(Integer(root, "times.est_time_enroute", "times.enroute_time")),
                TaxiTime = Duration(Integer(root, "times.taxi_out", "times.est_time_taxi")),
                FlightDate = departureUtc.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant(),
                DepartureTime = departureUtc.ToString("HHmm", CultureInfo.InvariantCulture)
            };

            string[] required =
            {
                data.Airline, data.FlightNumber, data.Departure, data.Destination,
                data.AircraftIcao, data.AircraftRegistration
            };
            if (required.Any(string.IsNullOrWhiteSpace) || data.BowKg <= 0 || data.BlockFuelKg <= 0)
            {
                throw new InvalidOperationException("SIMBRIEF OFP IS MISSING AIRCRAFT, FLIGHT, WEIGHT, OR FUEL DATA REQUIRED BY ELOADCONTROL.");
            }

            return data;
        }

        private static string Text(JObject root, params string[] paths)
        {
            foreach (string path in paths)
            {
                string value = root.SelectToken(path)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private static int Integer(JObject root, params string[] paths)
        {
            string value = Text(root, paths);
            return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        private static long Long(JObject root, params string[] paths)
        {
            string value = Text(root, paths);
            return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
        }

        private static int WeightKg(JObject root, double conversionFactor, params string[] paths)
        {
            string value = Text(root, paths);
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? Math.Max(0, (int)Math.Round(parsed * conversionFactor, MidpointRounding.AwayFromZero))
                : 0;
        }

        private static string Duration(int seconds)
        {
            int safeSeconds = Math.Max(0, seconds);
            int hours = safeSeconds / 3600;
            int minutes = (safeSeconds % 3600) / 60;
            return hours.ToString("00", CultureInfo.InvariantCulture) + minutes.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class SimbriefLoadsheetClient
    {
        private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private readonly HttpClient client;

        public SimbriefLoadsheetClient(HttpClient client = null)
        {
            this.client = client ?? SharedClient;
        }

        internal static string NormalizeUserIdentifier(string value)
        {
            string cleaned = (value ?? string.Empty).Trim();
            if (cleaned.Length == 0 || cleaned.Length > 64 || cleaned.Any(char.IsControl))
            {
                return string.Empty;
            }

            return cleaned;
        }

        internal static string BuildFetchUrl(string userIdentifier)
        {
            string cleaned = NormalizeUserIdentifier(userIdentifier);
            if (cleaned.Length == 0)
            {
                throw new InvalidOperationException("ENTER A SIMBRIEF USERNAME OR PILOT ID IN SETUP / ACCOUNT.");
            }

            string parameter = cleaned.All(char.IsDigit) ? "userid" : "username";
            return "https://www.simbrief.com/api/xml.fetcher.php?" + parameter + "=" + Uri.EscapeDataString(cleaned) + "&json=1";
        }

        public async Task<SimbriefLoadsheetData> FetchAsync(string username, string fallbackCallsign, CancellationToken token)
        {
            string url = BuildFetchUrl(username);
            using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("SIMBRIEF REQUEST FAILED (HTTP " + (int)response.StatusCode + ").");
            }

            return SimbriefLoadsheetData.Parse(json, fallbackCallsign);
        }
    }

    internal sealed class ELoadControlClient
    {
        private static readonly Uri BaseUri = new("https://www.eloadcontrol.com/api/v1/");
        private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(25) };
        private readonly HttpClient client;

        public ELoadControlClient(HttpClient client = null)
        {
            this.client = client ?? SharedClient;
        }

        public async Task<ELoadReferenceData> GetReferenceDataAsync(string apiKey, string aircraftIdentifier, CancellationToken token)
        {
            string identifier = Uri.EscapeDataString((aircraftIdentifier ?? string.Empty).Trim());
            Task<JObject> aircraftTask = SendAsync(HttpMethod.Get, "aircraft?identifier=" + identifier, apiKey, null, token);
            Task<JObject> formatsTask = SendAsync(HttpMethod.Get, "formats", apiKey, null, token);
            await Task.WhenAll(aircraftTask, formatsTask).ConfigureAwait(false);

            IReadOnlyList<ELoadAircraft> aircraft = (aircraftTask.Result["data"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => new ELoadAircraft
                {
                    Icao = item.Value<string>("icao") ?? string.Empty,
                    Type = item.Value<string>("type") ?? string.Empty,
                    CabinConfigurations = (item["availableCabinConfigs"] as JArray ?? new JArray())
                        .Values<string>()
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()
                })
                .ToArray();

            IReadOnlyList<ELoadFormat> formats = (formatsTask.Result["data"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => new ELoadFormat
                {
                    Name = item.Value<string>("name") ?? string.Empty,
                    TemplateId = item.Value<string>("templateId") ?? string.Empty
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.TemplateId))
                .ToArray();

            return new ELoadReferenceData { Aircraft = aircraft, Formats = formats };
        }

        public async Task<ELoadLoadsheetResult> GenerateLoadsheetAsync(string apiKey, JObject requestBody, CancellationToken token)
        {
            JObject result = await SendAsync(HttpMethod.Post, "generate-loadsheet", apiKey, requestBody, token).ConfigureAwait(false);
            return new ELoadLoadsheetResult
            {
                Loadsheet = result.Value<string>("loadsheet") ?? string.Empty,
                AcarsMessage = result.Value<string>("acarsMessage") ?? string.Empty,
                LoadPlanning = result.Value<string>("loadPlanning") ?? string.Empty,
                EditionNumber = result.Value<int?>("editionNumber") ?? requestBody?.Value<int?>("editionNumber") ?? 1
            };
        }

        private async Task<JObject> SendAsync(HttpMethod method, string relativePath, string apiKey, JObject body, CancellationToken token)
        {
            string key = (apiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                throw new ELoadControlException(HttpStatusCode.Unauthorized, "ELOADCONTROL API KEY IS REQUIRED.");
            }

            using HttpRequestMessage request = new(method, new Uri(BaseUri, relativePath));
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            if (body != null)
            {
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            }

            using HttpResponseMessage response = await client.SendAsync(request, token).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            JObject json = TryParseObject(responseText);
            if (!response.IsSuccessStatusCode)
            {
                string error = json?.Value<string>("error");
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "ELOADCONTROL REQUEST FAILED (HTTP " + (int)response.StatusCode + ").";
                }
                throw new ELoadControlException(response.StatusCode, error.ToUpperInvariant());
            }

            return json ?? throw new ELoadControlException(response.StatusCode, "ELOADCONTROL RETURNED INVALID JSON.");
        }

        private static JObject TryParseObject(string value)
        {
            try { return JObject.Parse(value ?? string.Empty); }
            catch { return null; }
        }
    }
}
