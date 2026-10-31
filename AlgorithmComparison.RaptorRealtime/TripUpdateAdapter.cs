using System.Globalization;
using System.Diagnostics;
using AlgorithmComparison.Core.Models;
using TransitRealtime;

namespace AlgorithmComparison.RaptorRealtime;

internal sealed class TripUpdateAdapter(TransitNetwork network, RealtimeRaptorOptions options,
    IReadOnlyDictionary<string, EffectiveTrip> baseline, IReadOnlyDictionary<string, int> stopIndex)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);

    public PreparedTimetable Build(FeedMessage feed, DateTimeOffset now, string version, out double routeGroupsMs)
    {
        routeGroupsMs = 0;
        if (feed.Header is not { HasTimestamp: true } header || header.GtfsRealtimeVersion != "2.0" ||
            header.Incrementality != FeedHeader.Types.Incrementality.FullDataset)
            throw new InvalidDataException("Expected a GTFS-RT 2.0 FULL_DATASET with a timestamp.");
        var generated = Timestamp(header.Timestamp);
        if (generated > now + options.FutureTolerance || now - generated > options.MaxAge)
            throw new InvalidDataException("Feed timestamp is stale or too far in the future.");
        var validUntil = generated + options.MaxAge;
        var trips = new Dictionary<string, EffectiveTrip>(baseline, StringComparer.Ordinal);
        var diagnostics = new List<UpdateDiagnostic>();
        var matched = 0;
        var applied = 0;
        var canceled = 0;
        var excluded = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in feed.Entity)
        {
            if (string.IsNullOrEmpty(entity.Id) || !entityIds.Add(entity.Id) || entity.IsDeleted || entity.TripUpdate?.Trip is null)
                throw new InvalidDataException("Expected unique, nondeleted TripUpdate entities.");
            var update = entity.TripUpdate;
            var descriptor = update.Trip;
            var id = descriptor.TripId;
            if (!baseline.TryGetValue(id, out var original))
            {
                diagnostics.Add(new("UnknownTrip", id, "Trip is absent from the selected static service day; update ignored."));
                continue;
            }
            // GZM omits start_date: allow that only for the local date of this full feed.
            var sameDate = descriptor.HasStartDate
                ? descriptor.StartDate == network.ServiceDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(generated, zone).DateTime) == network.ServiceDate;
            if (!sameDate || (descriptor.HasRouteId && descriptor.RouteId != original.RouteId) ||
                (descriptor.HasStartTime && ParseGtfsTime(descriptor.StartTime) != original.Stops[0].Departure))
            {
                diagnostics.Add(new("InstanceMismatch", id, "Date, route or original start time does not match; update ignored."));
                continue;
            }
            matched++;
            if (!seen.Add(id)) throw new InvalidDataException($"Multiple updates for trip {id} in one full feed.");
            var observed = update.HasTimestamp ? Timestamp(update.Timestamp) : generated;
            if (observed > now + options.FutureTolerance || now - observed > options.MaxAge)
            {
                diagnostics.Add(new("StaleTrip", id, "Trip observation is stale/invalid; using its scheduled times."));
                continue;
            }
            // Conservatively expire the entire view when its oldest applied observation expires.
            validUntil = observed + options.MaxAge < validUntil ? observed + options.MaxAge : validUntil;
            var relationship = (int)descriptor.ScheduleRelationship;
            if (relationship is 3 or 7) // CANCELED or DELETED
            {
                trips.Remove(id);
                canceled++;
                continue;
            }
            if (relationship != 0 || descriptor.ModifiedTrip is not null || update.TripProperties is not null)
            {
                trips.Remove(id);
                excluded++;
                diagnostics.Add(new("UnsupportedTrip", id, "Added/replacement/modified trip is not supported; excluded from this view."));
                continue;
            }
            try
            {
                trips[id] = Apply(original, update);
                applied++;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                trips.Remove(id);
                excluded++;
                diagnostics.Add(new("InvalidTripUpdate", id, ex.Message + " Trip excluded from this view."));
            }
        }
        if (feed.Entity.Count > 0 && matched == 0)
            throw new InvalidDataException("No trip instances match this static GTFS/service date. Check the base timetable.");
        var report = new RealtimeUpdateReport(true, version, generated, validUntil, applied, canceled,
            excluded, 0, diagnostics.AsReadOnly());
        var grouping = Stopwatch.StartNew();
        var prepared = PreparedTimetable.Build(trips, stopIndex, version, validUntil, report);
        routeGroupsMs = grouping.Elapsed.TotalMilliseconds;
        return prepared;
    }

    private EffectiveTrip Apply(EffectiveTrip original, TripUpdate update)
    {
        var updates = new Dictionary<int, TripUpdate.Types.StopTimeUpdate>();
        var lastIndex = -1;
        foreach (var stop in update.StopTimeUpdate)
        {
            var matches = original.Stops.Select((s, i) => (s, i)).Where(pair => stop.HasStopSequence
                ? pair.s.StopSequence == stop.StopSequence : stop.HasStopId && pair.s.StopId == stop.StopId).ToArray();
            if (matches.Length != 1 || (stop.HasStopId && stop.StopId != matches[0].s.StopId))
                throw new InvalidDataException("Stop identifier/sequence is missing, inconsistent or ambiguous.");
            var index = matches[0].i;
            if (index <= lastIndex) throw new InvalidDataException("Stop updates must be unique and ordered along the trip.");
            if (stop.StopTimeProperties is not null)
                throw new InvalidDataException("Assigned stop and pickup/drop-off modifications are unsupported.");
            lastIndex = index;
            updates.Add(index, stop);
        }
        int? propagatedDelay = update.HasDelay ? update.Delay : null;
        var stops = new EffectiveStopTime[original.Stops.Length];
        for (var i = 0; i < stops.Length; i++)
        {
            var scheduled = original.Stops[i];
            updates.TryGetValue(i, out var change);
            var relationship = change is null ? 0 : (int)change.ScheduleRelationship;
            if (relationship == 2) // NO_DATA ends propagation, but does not mean delay=0.
            {
                propagatedDelay = null;
                stops[i] = scheduled with { ArrivalOrigin = TimeOrigin.NoData, DepartureOrigin = TimeOrigin.NoData };
                continue;
            }
            if (relationship is not (0 or 1)) throw new InvalidDataException("Unsupported stop schedule relationship.");
            var arrival = Event(change?.Arrival, scheduled.Arrival);
            var departure = Event(change?.Departure, scheduled.Departure);
            if (relationship == 0 && change is not null && arrival is null && departure is null)
                throw new InvalidDataException("SCHEDULED stop update contains neither an arrival nor a departure prediction.");

            var a = arrival?.Time ?? (departure is { } dep
                ? checked(scheduled.Arrival + dep.Time - scheduled.Departure)
                : checked(scheduled.Arrival + (propagatedDelay ?? 0)));
            var d = departure?.Time ?? (arrival is { } arr
                ? checked(scheduled.Departure + arr.Time - scheduled.Arrival)
                : checked(scheduled.Departure + (propagatedDelay ?? 0)));
            var aOrigin = arrival?.Origin ?? (departure is not null ? TimeOrigin.EstimatedArrival
                : propagatedDelay.HasValue ? TimeOrigin.Propagated : TimeOrigin.Scheduled);
            var dOrigin = departure?.Origin ?? (arrival is not null ? TimeOrigin.EstimatedDeparture
                : propagatedDelay.HasValue ? TimeOrigin.Propagated : TimeOrigin.Scheduled);
            stops[i] = scheduled with
            {
                Arrival = a,
                Departure = d,
                CanBoard = relationship != 1,
                CanAlight = relationship != 1,
                ArrivalOrigin = aOrigin,
                DepartureOrigin = dOrigin
            };
            if (departure is not null) propagatedDelay = checked(d - scheduled.Departure);
            else if (arrival is not null) propagatedDelay = checked(a - scheduled.Arrival);
        }
        PreparedTimetable.Validate(stops); // Never silently clamp inconsistent predictions.
        return original with { Stops = stops };
    }

    private (int Time, TimeOrigin Origin)? Event(TripUpdate.Types.StopTimeEvent? value, int scheduled)
    {
        if (value is null) return null;
        if (value.HasTime) return (ServiceDayTime.ToSeconds(value.Time, network.ServiceDate, zone), TimeOrigin.ReportedTime);
        if (value.HasDelay) return (checked(scheduled + value.Delay), TimeOrigin.ReportedDelay);
        return null;
    }

    private static DateTimeOffset Timestamp(ulong value)
    {
        if (value > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new InvalidDataException("Invalid GTFS-RT timestamp.");
        return DateTimeOffset.FromUnixTimeSeconds((long)value);
    }

    private static int ParseGtfsTime(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) ||
            !int.TryParse(parts[2], out var s) || h < 0 || m is < 0 or > 59 || s is < 0 or > 59)
            throw new InvalidDataException("Invalid trip start_time.");
        return checked(h * 3600 + m * 60 + s);
    }
}
