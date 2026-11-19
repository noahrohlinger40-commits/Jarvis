// WeatherService.cs: answers "what's the weather" using the free Open-Meteo web services (no API key needed).
// Two calls: the geocoding API turns a place name into coordinates, then the forecast API returns current
// conditions for those coordinates. Results are formatted as a short bullet list in Fahrenheit, mph and inches.
// Note this makes ordinary internet requests, separate from the AI model settings.
using System.Globalization;
using System.Text.Json;

namespace Jarvis.Core;

// Lets the rest of Jarvis ask for weather without knowing which provider is used.
public interface IWeatherService
{
    Task<string> GetCurrentWeatherAsync(string location, CancellationToken cancellationToken);
}

// Weather lookup implemented with the Open-Meteo API.
public sealed class OpenMeteoWeatherService : IWeatherService
{
    // One shared HttpClient for the whole app (creating one per request wastes sockets); 20-second timeout.
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    // Returns the weather as ready-to-show text, or a short explanation when the place is empty, unknown or the lookup fails.
    public async Task<string> GetCurrentWeatherAsync(string location, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "Usage: weather <location>. You can also set `defaultWeatherLocation` in jarvis.settings.json.";
        }

        var resolvedLocation = await ResolveLocationAsync(location.Trim(), cancellationToken);

        if (resolvedLocation is null)
        {
            return $"No weather location match found for \"{location.Trim()}\".";
        }

        // Forecast request: coordinates in, the "current" values listed in &current= out. timezone=auto makes the time local to the place.
        // InvariantCulture keeps the decimal point a "." even on computers set to a language that uses a comma.
        var weatherUrl =
            $"https://api.open-meteo.com/v1/forecast?latitude={resolvedLocation.Latitude.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={resolvedLocation.Longitude.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m" +
            "&temperature_unit=fahrenheit" +
            "&wind_speed_unit=mph" +
            "&precipitation_unit=inch" +
            "&timezone=auto";

        using var weatherResponse = await HttpClient.GetAsync(weatherUrl, cancellationToken);
        var weatherBody = await weatherResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!weatherResponse.IsSuccessStatusCode)
        {
            return $"Weather lookup failed: HTTP {(int)weatherResponse.StatusCode}.";
        }

        using var weatherDocument = JsonDocument.Parse(weatherBody);
        var root = weatherDocument.RootElement;

        if (!root.TryGetProperty("current", out var current))
        {
            return $"Weather lookup failed for {resolvedLocation.DisplayName}.";
        }

        // The response also says which units each value uses; fall back to °F and mph if that section is missing.
        var currentUnits = root.TryGetProperty("current_units", out var units) ? units : default;
        var temperature = GetDouble(current, "temperature_2m");
        var apparentTemperature = GetDouble(current, "apparent_temperature");
        var weatherCode = GetInt32(current, "weather_code");
        var windSpeed = GetDouble(current, "wind_speed_10m");
        var windDirection = GetDouble(current, "wind_direction_10m");
        var observationTime = GetString(current, "time");
        var temperatureUnit = GetString(currentUnits, "temperature_2m") ?? "°F";
        var windUnit = GetString(currentUnits, "wind_speed_10m") ?? "mph";

        var lines = new List<string>
        {
            $"Current weather for {resolvedLocation.DisplayName}:",
            $"- Condition: {DescribeWeatherCode(weatherCode)}",
            $"- Temperature: {FormatNumber(temperature)}{temperatureUnit}",
            $"- Feels like: {FormatNumber(apparentTemperature)}{temperatureUnit}",
            $"- Wind: {FormatNumber(windSpeed)} {windUnit} {DescribeWindDirection(windDirection)}"
        };

        if (!string.IsNullOrWhiteSpace(observationTime))
        {
            lines.Add($"- Observed at: {observationTime} ({resolvedLocation.TimeZone})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // Geocoding: asks Open-Meteo for the best match (count=1) for the typed place name; null when nothing matches or the call fails.
    private static async Task<ResolvedWeatherLocation?> ResolveLocationAsync(string location, CancellationToken cancellationToken)
    {
        var geocodeUrl =
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(location)}&count=1&language=en&format=json";

        using var geocodeResponse = await HttpClient.GetAsync(geocodeUrl, cancellationToken);
        var geocodeBody = await geocodeResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!geocodeResponse.IsSuccessStatusCode)
        {
            return null;
        }

        using var geocodeDocument = JsonDocument.Parse(geocodeBody);
        var root = geocodeDocument.RootElement;

        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        var match = results[0];

        return new ResolvedWeatherLocation(
            match.GetProperty("latitude").GetDouble(),
            match.GetProperty("longitude").GetDouble(),
            BuildDisplayName(match),
            GetString(match, "timezone") ?? "local time");
    }

    // Builds a label like "Tallahassee, Florida, United States" from the name, region and country, skipping blanks and repeats.
    private static string BuildDisplayName(JsonElement element)
    {
        var parts = new[]
        {
            GetString(element, "name"),
            GetString(element, "admin1"),
            GetString(element, "country")
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", parts);
    }

    // Turns Open-Meteo's numeric WMO weather code (0 = clear sky, 61-65 = rain, 95 = thunderstorm...) into words.
    private static string DescribeWeatherCode(int code)
    {
        return code switch
        {
            0 => "Clear sky",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing drizzle",
            61 or 63 or 65 => "Rain",
            66 or 67 => "Freezing rain",
            71 or 73 or 75 or 77 => "Snow",
            80 or 81 or 82 => "Rain showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Thunderstorm with hail",
            _ => $"Weather code {code}"
        };
    }

    // Converts a compass angle in degrees to N, NE, E and so on by splitting the circle into eight 45-degree slices.
    private static string DescribeWindDirection(double degrees)
    {
        var directions = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var normalized = ((degrees % 360) + 360) % 360;
        var index = (int)Math.Round(normalized / 45d, MidpointRounding.AwayFromZero) % directions.Length;
        return directions[index];
    }

    // Shows at most one decimal place; NaN (value missing from the response) is shown as "n/a".
    private static string FormatNumber(double value)
    {
        return double.IsNaN(value) ? "n/a" : value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // The Get* helpers read a JSON property safely. Missing or wrong-typed values become null, NaN (doubles) or -1 (integers) instead of throwing.
    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return double.NaN;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value)
            ? value
            : double.NaN;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return -1;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : -1;
    }

    // A place found by geocoding: coordinates, a display name and its time zone.
    private sealed record ResolvedWeatherLocation(
        double Latitude,
        double Longitude,
        string DisplayName,
        string TimeZone);
}
