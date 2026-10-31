using System.Globalization;
using AlgorithmComparison.GTFS.Raw;

namespace AlgorithmComparison.GTFS;

internal static class GtfsParser
{
    public static IReadOnlyList<GtfsStop> ParseStops(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsStop>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsStop
            {
                StopId = Field(f, idx, "stop_id"),
                StopName = Field(f, idx, "stop_name"),
                StopLat = double.Parse(Field(f, idx, "stop_lat"), CultureInfo.InvariantCulture),
                StopLon = double.Parse(Field(f, idx, "stop_lon"), CultureInfo.InvariantCulture),
                ParentStation = OptionalField(f, idx, "parent_station"),
            });
        }
        return results;
    }

    internal static IEnumerable<(string Key, string Value)> ParseKeyValueColumns(
        TextReader reader,
        string keyColumn,
        string valueColumn)
    {
        var idx = ReadHeader(reader);
        if (!idx.TryGetValue(keyColumn, out int keyIndex))
            throw new InvalidDataException($"Required GTFS column '{keyColumn}' not found.");
        if (!idx.TryGetValue(valueColumn, out int valueIndex))
            throw new InvalidDataException($"Required GTFS column '{valueColumn}' not found.");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitLine(line);
            if (keyIndex >= fields.Length || valueIndex >= fields.Length)
                throw new InvalidDataException("GTFS row does not contain all required columns.");

            yield return (fields[keyIndex].Trim(), fields[valueIndex].Trim());
        }
    }

    public static IReadOnlyList<GtfsRoute> ParseRoutes(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsRoute>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsRoute
            {
                RouteId = Field(f, idx, "route_id"),
                RouteShortName = Field(f, idx, "route_short_name"),
                RouteLongName = OptionalField(f, idx, "route_long_name") ?? string.Empty,
                RouteType = int.Parse(Field(f, idx, "route_type"), CultureInfo.InvariantCulture),
            });
        }
        return results;
    }

    public static IReadOnlyList<GtfsTrip> ParseTrips(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsTrip>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsTrip
            {
                TripId = Field(f, idx, "trip_id"),
                RouteId = Field(f, idx, "route_id"),
                ServiceId = Field(f, idx, "service_id"),
                TripHeadsign = OptionalField(f, idx, "trip_headsign") ?? string.Empty,
            });
        }
        return results;
    }

    public static IReadOnlyList<GtfsStopTime> ParseStopTimes(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsStopTime>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsStopTime
            {
                TripId = Field(f, idx, "trip_id"),
                StopId = Field(f, idx, "stop_id"),
                StopSequence = int.Parse(Field(f, idx, "stop_sequence"), CultureInfo.InvariantCulture),
                ArrivalTime = ParseTimeToSeconds(Field(f, idx, "arrival_time")),
                DepartureTime = ParseTimeToSeconds(Field(f, idx, "departure_time")),
            });
        }
        return results;
    }

    public static IReadOnlyList<GtfsCalendarDate> ParseCalendarDates(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsCalendarDate>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsCalendarDate
            {
                ServiceId = Field(f, idx, "service_id"),
                Date = ParseGtfsDate(Field(f, idx, "date")),
                ExceptionType = int.Parse(Field(f, idx, "exception_type"), CultureInfo.InvariantCulture),
            });
        }
        return results;
    }

    public static IReadOnlyList<GtfsCalendar> ParseCalendar(TextReader reader)
    {
        var idx = ReadHeader(reader);
        var results = new List<GtfsCalendar>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitLine(line);
            results.Add(new GtfsCalendar
            {
                ServiceId = Field(f, idx, "service_id"),
                Monday = int.Parse(Field(f, idx, "monday"), CultureInfo.InvariantCulture),
                Tuesday = int.Parse(Field(f, idx, "tuesday"), CultureInfo.InvariantCulture),
                Wednesday = int.Parse(Field(f, idx, "wednesday"), CultureInfo.InvariantCulture),
                Thursday = int.Parse(Field(f, idx, "thursday"), CultureInfo.InvariantCulture),
                Friday = int.Parse(Field(f, idx, "friday"), CultureInfo.InvariantCulture),
                Saturday = int.Parse(Field(f, idx, "saturday"), CultureInfo.InvariantCulture),
                Sunday = int.Parse(Field(f, idx, "sunday"), CultureInfo.InvariantCulture),
                StartDate = ParseGtfsDate(Field(f, idx, "start_date")),
                EndDate = ParseGtfsDate(Field(f, idx, "end_date")),
            });
        }
        return results;
    }
    public static int ParseTimeToSeconds(string gtfsTime)
    {
        gtfsTime = gtfsTime.Trim();
        // Format: H+:MM:SS
        int first = gtfsTime.IndexOf(':');
        int last = gtfsTime.LastIndexOf(':');
        int h = int.Parse(gtfsTime[..first], CultureInfo.InvariantCulture);
        int m = int.Parse(gtfsTime[(first + 1)..last], CultureInfo.InvariantCulture);
        int s = int.Parse(gtfsTime[(last + 1)..], CultureInfo.InvariantCulture);
        return h * 3600 + m * 60 + s;
    }

    private static Dictionary<string, int> ReadHeader(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("Empty GTFS file.");
        // Strip UTF-8 BOM character that some tools emit without a BOM byte marker.
        if (header.Length > 0 && header[0] == '﻿')
            header = header[1..];
        var fields = SplitLine(header);
        var map = new Dictionary<string, int>(fields.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Length; i++)
            map[fields[i].Trim()] = i;
        return map;
    }

    private static string Field(string[] fields, Dictionary<string, int> idx, string name)
    {
        if (!idx.TryGetValue(name, out int i) || i >= fields.Length)
            throw new InvalidDataException($"Required GTFS column '{name}' not found.");
        return fields[i];
    }

    private static string? OptionalField(string[] fields, Dictionary<string, int> idx, string name)
    {
        if (!idx.TryGetValue(name, out int i) || i >= fields.Length) return null;
        var v = fields[i];
        return string.IsNullOrEmpty(v) ? null : v;
    }
    private static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(string.Empty); break; }

            if (line[i] == '"')
            {
                i++; // skip opening quote
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        i++;
                        if (i < line.Length && line[i] == '"') { sb.Append('"'); i++; } // "" → "
                        else break; // end of quoted field
                    }
                    else { sb.Append(line[i++]); }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                fields.Add(line[start..i]);
                if (i < line.Length) i++;
            }
        }
        return fields.ToArray();
    }

    private static DateOnly ParseGtfsDate(string yyyymmdd)
    {
        yyyymmdd = yyyymmdd.Trim();
        int year = int.Parse(yyyymmdd[..4], CultureInfo.InvariantCulture);
        int month = int.Parse(yyyymmdd[4..6], CultureInfo.InvariantCulture);
        int day = int.Parse(yyyymmdd[6..8], CultureInfo.InvariantCulture);
        return new DateOnly(year, month, day);
    }
}
