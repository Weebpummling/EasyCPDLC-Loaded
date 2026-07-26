using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyCPDLC
{
    /// <summary>
    /// Where a CDU METAR/ATIS request is sourced from. VATSIM is served by the
    /// existing Hoppie INFOREQ datalink path; REAL LIFE and SAYINTENTIONS are
    /// fetched directly over HTTP and written straight into the inbox.
    /// </summary>
    internal enum Vns430WeatherSource
    {
        Vatsim,
        RealWorld,
        SayIntentions
    }

    /// <summary>
    /// The ATC network the datalink connects to. Drives which credentials are required
    /// and the default weather source. IVAO is planned once its datalink is understood.
    /// </summary>
    internal enum Vns430AtcNetwork
    {
        Vatsim,
        SayIntentions
    }

    internal sealed class Vns430WeatherException : Exception
    {
        public Vns430WeatherException(string message) : base(message) { }
    }

    /// <summary>
    /// Fetches real-world and SayIntentions weather/ATIS for the CDU. The SayIntentions
    /// API key is passed in by the caller from the protected settings store; it is never
    /// hardcoded here.
    /// </summary>
    internal sealed class Vns430WeatherClient
    {
        private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private readonly HttpClient client;

        public Vns430WeatherClient(HttpClient client = null)
        {
            this.client = client ?? SharedClient;
        }

        internal static Vns430WeatherSource ParseSource(string value)
        {
            string v = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (v.StartsWith("REAL", StringComparison.Ordinal))
            {
                return Vns430WeatherSource.RealWorld;
            }
            // "SI" is what the instruments display and what the per-request VIA field
            // stores; "SAYINTENTIONS" is the token older settings were written with.
            if (v == "SI" || v.StartsWith("SAY", StringComparison.Ordinal))
            {
                return Vns430WeatherSource.SayIntentions;
            }
            return Vns430WeatherSource.Vatsim;
        }

        // Display label only. The stored override token stays "SAYINTENTIONS"
        // (ParseSource matches the "SAY" prefix), so existing settings keep parsing.
        internal static string SourceLabel(Vns430WeatherSource source) => source switch
        {
            Vns430WeatherSource.RealWorld => "REAL WORLD",
            Vns430WeatherSource.SayIntentions => "SI",
            _ => "VATSIM"
        };

        internal async Task<string> FetchMetarAsync(
            Vns430WeatherSource source,
            string icao,
            string sayIntentionsApiKey,
            CancellationToken token)
        {
            string station = NormalizeIcao(icao);
            return source switch
            {
                Vns430WeatherSource.SayIntentions => await FetchSayIntentionsMetarAsync(station, sayIntentionsApiKey, token).ConfigureAwait(false),
                _ => await FetchRealWorldMetarAsync(station, token).ConfigureAwait(false)
            };
        }

        internal async Task<string> FetchAtisAsync(
            Vns430WeatherSource source,
            string icao,
            string type,
            string sayIntentionsApiKey,
            CancellationToken token)
        {
            string station = NormalizeIcao(icao);
            return source switch
            {
                Vns430WeatherSource.SayIntentions => await FetchSayIntentionsAtisAsync(station, sayIntentionsApiKey, token).ConfigureAwait(false),
                _ => await FetchRealWorldAtisAsync(station, type, token).ConfigureAwait(false)
            };
        }

        // ---- Real world (aviationweather.gov + datis.clowd.io) -----------------

        private async Task<string> FetchRealWorldMetarAsync(string icao, CancellationToken token)
        {
            string metar = (await GetTextAsync(
                "https://aviationweather.gov/api/data/metar?ids=" + icao + "&format=raw", token).ConfigureAwait(false)).Trim();
            if (metar.Length == 0)
            {
                throw new Vns430WeatherException("NO METAR FOR " + icao);
            }

            string taf = string.Empty;
            try
            {
                taf = (await GetTextAsync(
                    "https://aviationweather.gov/api/data/taf?ids=" + icao + "&format=raw", token).ConfigureAwait(false)).Trim();
            }
            catch (Exception)
            {
                // A missing TAF must not fail the METAR request.
            }

            StringBuilder body = new();
            body.Append(icao).Append(" METAR (REAL WORLD)\n").Append(metar);
            if (taf.Length > 0)
            {
                body.Append("\n\nTAF\n").Append(taf);
            }
            return body.ToString();
        }

        private async Task<string> FetchRealWorldAtisAsync(string icao, string type, CancellationToken token)
        {
            string json = await GetTextAsync("https://datis.clowd.io/api/" + icao, token).ConfigureAwait(false);
            JToken parsed;
            try
            {
                parsed = JToken.Parse(json);
            }
            catch (Exception)
            {
                throw new Vns430WeatherException("NO D-ATIS FOR " + icao);
            }

            if (parsed is JObject obj && obj["error"] != null)
            {
                throw new Vns430WeatherException("NO D-ATIS FOR " + icao);
            }
            if (parsed is not JArray entries || entries.Count == 0)
            {
                throw new Vns430WeatherException("NO D-ATIS FOR " + icao);
            }

            bool wantArrival = (type ?? string.Empty).StartsWith("ARR", StringComparison.OrdinalIgnoreCase);
            JToken chosen = entries.FirstOrDefault(e =>
                    string.Equals((string)e["type"], wantArrival ? "arr" : "dep", StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(e =>
                    string.Equals((string)e["type"], "combined", StringComparison.OrdinalIgnoreCase))
                ?? entries.First();

            string text = ((string)chosen["datis"] ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                throw new Vns430WeatherException("NO D-ATIS FOR " + icao);
            }

            string atisType = ((string)chosen["type"] ?? string.Empty).ToUpperInvariant();
            string header = icao + " D-ATIS" + (atisType.Length > 0 ? " " + atisType : string.Empty) + " (REAL WORLD)";
            return header + "\n" + text.ToUpperInvariant();
        }

        // ---- SayIntentions (getWX) --------------------------------------------

        private async Task<JObject> FetchSayIntentionsAirportAsync(string icao, string apiKey, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new Vns430WeatherException("SET SI KEY FIRST");
            }

            string url = "https://apipri.sayintentions.ai/sapi/getWX?api_key=" +
                Uri.EscapeDataString(apiKey) + "&icao=" + icao;
            string json = await GetTextAsync(url, token).ConfigureAwait(false);

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                throw new Vns430WeatherException("BAD SAYINTENTIONS REPLY");
            }

            JArray airports = root["airports"] as JArray;
            if (airports == null || airports.Count == 0 || airports[0] is not JObject airport)
            {
                throw new Vns430WeatherException("NO WX FOR " + icao);
            }
            return airport;
        }

        private async Task<string> FetchSayIntentionsMetarAsync(string icao, string apiKey, CancellationToken token)
        {
            JObject airport = await FetchSayIntentionsAirportAsync(icao, apiKey, token).ConfigureAwait(false);
            string metar = ((string)airport["metar"] ?? string.Empty).Trim();
            if (metar.Length == 0)
            {
                throw new Vns430WeatherException("NO METAR FOR " + icao);
            }

            string taf = ((string)airport["taf"] ?? string.Empty).Trim();
            StringBuilder body = new();
            body.Append(icao).Append(" METAR (SAYINTENTIONS)\n").Append(metar);
            if (taf.Length > 0)
            {
                body.Append("\n\nTAF\n").Append(taf);
            }
            return body.ToString();
        }

        private async Task<string> FetchSayIntentionsAtisAsync(string icao, string apiKey, CancellationToken token)
        {
            JObject airport = await FetchSayIntentionsAirportAsync(icao, apiKey, token).ConfigureAwait(false);

            // atis_cpdlc is the datalink-formatted (all caps) variant; fall back to the
            // spoken atis text if it is missing.
            string atis = ((string)airport["atis_cpdlc"] ?? string.Empty).Trim();
            if (atis.Length == 0)
            {
                atis = ((string)airport["atis"] ?? string.Empty).Trim().ToUpperInvariant();
            }
            if (atis.Length == 0)
            {
                throw new Vns430WeatherException("NO ATIS FOR " + icao);
            }

            string arr = ((string)airport["active_runways_arriving"] ?? string.Empty).Trim();
            string dep = ((string)airport["active_runways_departing"] ?? string.Empty).Trim();

            StringBuilder body = new();
            body.Append(icao).Append(" ATIS (SAYINTENTIONS)\n").Append(atis);
            if (arr.Length > 0 || dep.Length > 0)
            {
                body.Append("\n\nRWY");
                if (arr.Length > 0)
                {
                    body.Append(" ARR ").Append(arr);
                }
                if (dep.Length > 0)
                {
                    body.Append(" DEP ").Append(dep);
                }
            }
            return body.ToString();
        }

        // ---- helpers ----------------------------------------------------------

        private static string NormalizeIcao(string icao)
        {
            string clean = (icao ?? string.Empty).Trim().ToUpperInvariant();
            if (clean.Length != 4)
            {
                throw new Vns430WeatherException("STATION MUST BE 4 CHAR");
            }
            return clean;
        }

        private async Task<string> GetTextAsync(string url, CancellationToken token)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("EasyCPDLC-VNS430/1.0");
            using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new Vns430WeatherException("WX SOURCE ERR " + (int)response.StatusCode);
            }
            return text ?? string.Empty;
        }
    }
}
