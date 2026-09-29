using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// MDM-001-OPERATOR-LOADER: strict validation of a reviewed master-data document before any database
/// access. The format is JSON (<c>paquetenvia.master-data.v1</c>): service areas and zones carry GeoJSON
/// MultiPolygons and nested references, and a JSON number token keeps its exact spelling, so an integer
/// cents amount is distinguishable from <c>120.0</c> or <c>1.2e4</c> (CSV would carry every value as
/// locale-sensitive text and could not nest polygons). Every property is required, unknown or duplicated
/// properties are refused, and the loader function re-validates everything in PostgreSQL. Errors carry only
/// a reference (<c>section[n].field</c>) and a code, never a value, so no name, identifier or coordinate
/// reaches the console.
/// </summary>
internal static partial class MasterDataDocumentValidator
{
    internal const string Format = "paquetenvia.master-data.v1";
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    internal const int MaximumSectionEntries = 5000;
    internal const int MaximumDriverServiceAreas = 100;
    internal const int MaximumVertices = 200_000;
    internal const int MaximumTotalEntries = 20_000;

    /// <summary>MDM-001 M2: the IANA zones a new Mexican city may use; the loader function holds the same list.</summary>
    internal static readonly string[] MexicoTimeZones =
    [
        "America/Bahia_Banderas", "America/Cancun", "America/Chihuahua", "America/Ciudad_Juarez",
        "America/Hermosillo", "America/Matamoros", "America/Mazatlan", "America/Merida",
        "America/Mexico_City", "America/Monterrey", "America/Ojinaga", "America/Tijuana",
    ];
    private const int MaximumErrors = 50;

    internal static readonly string[] Sections =
        ["cities", "service_areas", "operating_zones", "tariff_rules", "driver_profiles"];

    internal static MasterDataValidationResult Validate(ReadOnlySpan<byte> content, Guid organizationId)
    {
        var errors = new List<string>();
        var sha256 = SHA256.HashData(content);
        if (content.Length == 0 || content.Length > MaximumFileBytes)
        {
            errors.Add($"document: MDM001_FILE_SIZE (1..{MaximumFileBytes} bytes)");
            return new MasterDataValidationResult(errors, sha256, new Dictionary<string, int>(), 0);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException)
        {
            errors.Add("document: MDM001_NOT_JSON");
            return new MasterDataValidationResult(errors, sha256, new Dictionary<string, int>(), 0);
        }

        using (document)
        {
            var validator = new State(errors, organizationId);
            validator.Document(document.RootElement);
            var counts = Sections.ToDictionary(
                section => section,
                section => document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(section, out var array) &&
                    array.ValueKind == JsonValueKind.Array
                        ? array.GetArrayLength()
                        : 0,
                StringComparer.Ordinal);
            return new MasterDataValidationResult(
                errors.Count > MaximumErrors ? errors.Take(MaximumErrors).Append("document: MDM001_TOO_MANY_ERRORS").ToList() : errors,
                sha256,
                counts,
                validator.DriverServiceAreaCount);
        }
    }

    private sealed class State(List<string> errors, Guid organizationId)
    {
        private readonly HashSet<string> _cities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _areas = new(StringComparer.Ordinal);
        private readonly HashSet<string> _zones = new(StringComparer.Ordinal);
        private readonly HashSet<string> _tariffs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _drivers = new(StringComparer.Ordinal);
        private readonly List<TariffWindow> _activeWindows = [];
        private int _vertices;

        internal int DriverServiceAreaCount { get; private set; }

        internal void Document(JsonElement root)
        {
            if (!Shape(root, "document", ["format", "classification", "owner_org_id", .. Sections]))
            {
                return;
            }

            if (!IsString(root, "format", out var format) || format != Format)
            {
                Error("document.format", "MDM001_FORMAT");
            }

            if (!IsString(root, "classification", out var classification) ||
                classification is not ("SYNTHETIC" or "REVIEWED"))
            {
                Error("document.classification", "MDM001_CLASSIFICATION");
            }

            if (!IsString(root, "owner_org_id", out var owner) ||
                !CanonicalUuid().IsMatch(owner) ||
                !string.Equals(owner, organizationId.ToString("D"), StringComparison.Ordinal))
            {
                Error("document.owner_org_id", "MDM001_OWNER_ORGANIZATION_MISMATCH");
            }

            foreach (var section in Sections)
            {
                var array = root.GetProperty(section);
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumSectionEntries)
                {
                    Error(section, "MDM001_SECTION_SHAPE");
                }
            }

            if (errors.Count != 0)
            {
                return;
            }

            var total = Sections.Sum(section => root.GetProperty(section).GetArrayLength()) +
                root.GetProperty("driver_profiles").EnumerateArray()
                    .Where(profile => profile.ValueKind == JsonValueKind.Object &&
                        profile.TryGetProperty("service_areas", out var areas) && areas.ValueKind == JsonValueKind.Array)
                    .Sum(profile => profile.GetProperty("service_areas").GetArrayLength());
            if (total > MaximumTotalEntries)
            {
                Error("document", "MDM001_DOCUMENT_TOO_LARGE");
                return;
            }

            Each(root, "cities", City);
            Each(root, "service_areas", ServiceArea);
            Each(root, "operating_zones", OperatingZone);
            Each(root, "tariff_rules", TariffRule);
            TariffOverlaps();
            Each(root, "driver_profiles", DriverProfile);
        }

        /// <summary>
        /// MDM-001 M3, within the file: ACTIVE rules of one (city, area, zone, tier, service type) must not
        /// overlap in [active_from, active_to). The loader function repeats this against stored rules.
        /// </summary>
        private void TariffOverlaps()
        {
            foreach (var group in _activeWindows.GroupBy(window => window.Key, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(window => window.From).ThenBy(window => window.Index).ToArray();
                // The latest end seen so far (null = open-ended): any later start before it overlaps.
                var reach = ordered[0].To;
                var open = ordered[0].To is null;
                for (var index = 1; index < ordered.Length; index++)
                {
                    if (open || ordered[index].From < reach)
                    {
                        Error(ordered[index].Reference, "MDM001_TARIFF_OVERLAP");
                    }

                    open |= ordered[index].To is null;
                    if (!open && ordered[index].To > reach)
                    {
                        reach = ordered[index].To;
                    }
                }
            }
        }

        private void Each(JsonElement root, string section, Action<JsonElement, string> validate)
        {
            var index = 0;
            foreach (var item in root.GetProperty(section).EnumerateArray())
            {
                index++;
                validate(item, $"{section}[{index}]");
            }
        }

        private void City(JsonElement item, string reference)
        {
            // New cities are always ACTIVE, so the entry has no status; only a PLATFORM organization's load
            // may carry cities (enforced by the loader function, which knows the organization type).
            if (!Shape(item, reference, ["country_code", "state_code", "name", "timezone"]))
            {
                return;
            }

            var key = CityKey(item, reference);
            Name(item, "name", reference);
            if (IsString(item, "country_code", out var country) && country != "MX")
            {
                Error($"{reference}.country_code", "MDM001_CITY_COUNTRY_NOT_SUPPORTED");
            }

            if (!IsString(item, "timezone", out var timezone) || !MexicoTimeZones.Contains(timezone, StringComparer.Ordinal))
            {
                Error($"{reference}.timezone", "MDM001_CITY_TIMEZONE_NOT_ALLOWED");
            }

            if (key is not null && !_cities.Add(key))
            {
                Error(reference, "MDM001_DUPLICATE_NATURAL_KEY");
            }
        }

        private void ServiceArea(JsonElement item, string reference)
        {
            if (!Shape(item, reference, ["city", "name", "status", "polygon"]))
            {
                return;
            }

            var city = CityReference(item, "city", reference);
            var name = Name(item, "name", reference);
            Enum(item, "status", reference, "ACTIVE", "INACTIVE");
            MultiPolygon(item.GetProperty("polygon"), $"{reference}.polygon");
            if (city is not null && name is not null && !_areas.Add($"{city}|{name}"))
            {
                Error(reference, "MDM001_DUPLICATE_NATURAL_KEY");
            }
        }

        private void OperatingZone(JsonElement item, string reference)
        {
            if (!Shape(item, reference, ["service_area", "name", "zone_type", "status", "polygon"]))
            {
                return;
            }

            var area = ServiceAreaReference(item.GetProperty("service_area"), $"{reference}.service_area");
            var name = Name(item, "name", reference);
            Enum(item, "zone_type", reference, "CORE", "STANDARD", "EXTENDED", "EXCLUDED");
            Enum(item, "status", reference, "ACTIVE", "INACTIVE");
            MultiPolygon(item.GetProperty("polygon"), $"{reference}.polygon");
            if (area is not null && name is not null && !_zones.Add($"{area}|{name}"))
            {
                Error(reference, "MDM001_DUPLICATE_NATURAL_KEY");
            }
        }

        private void TariffRule(JsonElement item, string reference)
        {
            if (!Shape(item, reference,
                    ["city", "service_area", "operating_zone", "pricing_tier", "service_type", "amount_cents",
                     "tax_mode", "policy_version", "active_from", "active_to", "status"]))
            {
                return;
            }

            var city = CityReference(item, "city", reference);
            var area = OptionalName(item, "service_area", reference);
            var zone = OptionalName(item, "operating_zone", reference);
            if (zone.Present && zone.Value is not null && area.Value is null)
            {
                Error($"{reference}.operating_zone", "MDM001_ZONE_REQUIRES_SERVICE_AREA");
            }

            var tier = Enum(item, "pricing_tier", reference,
                "OCCASIONAL", "BUSINESS_1_49", "BUSINESS_50_199", "BUSINESS_200_499", "BUSINESS_500_PLUS", "CUSTOM");
            var serviceType = Enum(item, "service_type", reference, "SAME_DAY", "URGENT", "SCHEDULED_ROUTE");
            // AI-06 vocabulary. GATE-011-VAT-INCLUDED-2026-09-29: the database creates only VAT_INCLUDED rules
            // (MDM001_TARIFF_TAX_MODE_NOT_ALLOWED); another value may only name a stored rule being closed, which
            // only the database can tell.
            Enum(item, "tax_mode", reference, "PLUS_VAT", "VAT_INCLUDED", "EXEMPT");
            var status = Enum(item, "status", reference, "ACTIVE", "INACTIVE");

            // PRC-POLICY-VERSION-PER-ORG: required on every rule and stored in pricing.tariff_rules.policy_version;
            // the database refuses to change the version of a stored rule (MDM001_TARIFF_POLICY_VERSION_IMMUTABLE).
            if (!IsString(item, "policy_version", out var policyVersion) || !PolicyVersion().IsMatch(policyVersion))
            {
                Error($"{reference}.policy_version", "MDM001_POLICY_VERSION");
            }

            // AI-01 §4.15: money is integer cents only. The raw token must be a plain non-negative integer,
            // so 120.0, 1.2e4, -5, "120" and anything beyond int64 are refused before reaching PostgreSQL.
            var amount = item.GetProperty("amount_cents");
            if (amount.ValueKind != JsonValueKind.Number ||
                !IntegerCents().IsMatch(amount.GetRawText()) ||
                !long.TryParse(amount.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                Error($"{reference}.amount_cents", "MDM001_AMOUNT_NOT_INTEGER_CENTS");
            }

            var from = Instant(item.GetProperty("active_from"), $"{reference}.active_from", required: true);
            var to = Instant(item.GetProperty("active_to"), $"{reference}.active_to", required: false);
            if (from is not null && to is not null && to <= from)
            {
                Error($"{reference}.active_to", "MDM001_ACTIVE_WINDOW");
            }

            if (city is not null && tier is not null && serviceType is not null && from is not null &&
                !_tariffs.Add($"{city}|{area.Value}|{zone.Value}|{tier}|{serviceType}|{from:O}"))
            {
                Error(reference, "MDM001_DUPLICATE_NATURAL_KEY");
            }

            if (city is not null && tier is not null && serviceType is not null && from is not null &&
                status == "ACTIVE" && (to is null || to > from))
            {
                _activeWindows.Add(new TariffWindow(
                    $"{city}|{area.Value}|{zone.Value}|{tier}|{serviceType}", from.Value, to, _activeWindows.Count, reference));
            }
        }

        private void DriverProfile(JsonElement item, string reference)
        {
            if (!Shape(item, reference, ["user_id", "home_city", "driver_type", "vehicle_type", "status", "service_areas"]))
            {
                return;
            }

            if (!IsString(item, "user_id", out var userId) || !CanonicalUuid().IsMatch(userId))
            {
                Error($"{reference}.user_id", "MDM001_USER_ID");
            }
            else if (!_drivers.Add(userId))
            {
                Error(reference, "MDM001_DUPLICATE_NATURAL_KEY");
            }

            CityReference(item, "home_city", reference);
            Enum(item, "driver_type", reference, "OWN", "EXTERNAL", "ALLY");
            Enum(item, "vehicle_type", reference, "MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER");
            Enum(item, "status", reference, "PENDING", "ACTIVE", "SUSPENDED", "INACTIVE");
            var areas = item.GetProperty("service_areas");
            if (areas.ValueKind != JsonValueKind.Array || areas.GetArrayLength() > MaximumDriverServiceAreas)
            {
                Error($"{reference}.service_areas", "MDM001_SECTION_SHAPE");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var area in areas.EnumerateArray())
            {
                index++;
                DriverServiceAreaCount++;
                var areaReference = $"{reference}.service_areas[{index}]";
                if (!Shape(area, areaReference, ["city", "name", "status"]))
                {
                    continue;
                }

                var city = CityReference(area, "city", areaReference);
                var name = Name(area, "name", areaReference);
                Enum(area, "status", areaReference, "ACTIVE", "INACTIVE");
                if (city is not null && name is not null && !seen.Add($"{city}|{name}"))
                {
                    Error(areaReference, "MDM001_DUPLICATE_NATURAL_KEY");
                }
            }
        }

        private string? ServiceAreaReference(JsonElement element, string reference)
        {
            if (!Shape(element, reference, ["city", "name"]))
            {
                return null;
            }

            var city = CityReference(element, "city", reference);
            var name = Name(element, "name", reference);
            return city is null || name is null ? null : $"{city}|{name}";
        }

        private string? CityReference(JsonElement parent, string property, string reference)
        {
            var element = parent.GetProperty(property);
            var cityReference = $"{reference}.{property}";
            if (!Shape(element, cityReference, ["country_code", "state_code", "name"]))
            {
                return null;
            }

            var key = CityKey(element, cityReference);
            return Name(element, "name", cityReference) is null ? null : key;
        }

        private string? CityKey(JsonElement element, string reference)
        {
            var valid = true;
            if (!IsString(element, "country_code", out var country) || !CountryCode().IsMatch(country))
            {
                Error($"{reference}.country_code", "MDM001_COUNTRY_CODE");
                valid = false;
            }

            if (!IsString(element, "state_code", out var state) || !StateCode().IsMatch(state))
            {
                Error($"{reference}.state_code", "MDM001_STATE_CODE");
                valid = false;
            }

            return valid && IsString(element, "name", out var name) ? $"{country}|{state}|{name}" : null;
        }

        private string? Name(JsonElement parent, string property, string reference)
        {
            if (!IsString(parent, property, out var value) || !IsValidName(value))
            {
                Error($"{reference}.{property}", "MDM001_NAME");
                return null;
            }

            return value;
        }

        private (bool Present, string? Value) OptionalName(JsonElement parent, string property, string reference)
        {
            var element = parent.GetProperty(property);
            if (element.ValueKind == JsonValueKind.Null)
            {
                return (true, null);
            }

            if (element.ValueKind != JsonValueKind.String || !IsValidName(element.GetString()!))
            {
                Error($"{reference}.{property}", "MDM001_NAME");
                return (false, null);
            }

            return (true, element.GetString());
        }

        private string? Enum(JsonElement parent, string property, string reference, params string[] allowed)
        {
            if (!IsString(parent, property, out var value) || !allowed.Contains(value, StringComparer.Ordinal))
            {
                Error($"{reference}.{property}", "MDM001_ENUM");
                return null;
            }

            return value;
        }

        private DateTimeOffset? Instant(JsonElement element, string reference, bool required)
        {
            if (element.ValueKind == JsonValueKind.Null && !required)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.String ||
                !UtcInstant().IsMatch(element.GetString()!) ||
                !DateTimeOffset.TryParseExact(element.GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var instant))
            {
                Error(reference, "MDM001_INSTANT");
                return null;
            }

            return instant;
        }

        /// <summary>
        /// GeoJSON MultiPolygon in WGS84 longitude/latitude: exactly <c>type</c> and <c>coordinates</c>, every
        /// ring closed with at least four positions, every position two finite in-range numbers. Topological
        /// validity (self-intersection, ring orientation, holes) and the zone-within-area rule are checked by
        /// PostGIS inside the loader function before anything is written.
        /// </summary>
        private void MultiPolygon(JsonElement element, string reference)
        {
            if (!Shape(element, reference, ["type", "coordinates"]))
            {
                return;
            }

            if (!IsString(element, "type", out var type) || type != "MultiPolygon")
            {
                Error($"{reference}.type", "MDM001_GEOMETRY_TYPE");
                return;
            }

            var polygons = element.GetProperty("coordinates");
            if (polygons.ValueKind != JsonValueKind.Array || polygons.GetArrayLength() == 0)
            {
                Error(reference, "MDM001_GEOMETRY_INVALID");
                return;
            }

            foreach (var polygon in polygons.EnumerateArray())
            {
                if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0)
                {
                    Error(reference, "MDM001_GEOMETRY_INVALID");
                    return;
                }

                foreach (var ring in polygon.EnumerateArray())
                {
                    if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
                    {
                        Error(reference, "MDM001_GEOMETRY_INVALID");
                        return;
                    }

                    (double X, double Y)? first = null;
                    (double X, double Y) last = default;
                    foreach (var position in ring.EnumerateArray())
                    {
                        if (++_vertices > MaximumVertices)
                        {
                            Error(reference, "MDM001_GEOMETRY_TOO_LARGE");
                            return;
                        }

                        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 2 ||
                            !Coordinate(position[0], 180, out var x) || !Coordinate(position[1], 90, out var y))
                        {
                            Error(reference, "MDM001_GEOMETRY_INVALID");
                            return;
                        }

                        first ??= (x, y);
                        last = (x, y);
                    }

                    if (first != last)
                    {
                        Error(reference, "MDM001_GEOMETRY_RING_NOT_CLOSED");
                        return;
                    }
                }
            }
        }

        private static bool Coordinate(JsonElement element, double bound, out double value)
        {
            value = 0;
            return element.ValueKind == JsonValueKind.Number &&
                element.TryGetDouble(out value) &&
                double.IsFinite(value) &&
                value >= -bound && value <= bound;
        }

        /// <summary>The element is an object with exactly these properties, each once.</summary>
        private bool Shape(JsonElement element, string reference, string[] expected)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(reference, "MDM001_ENTRY_SHAPE");
                return false;
            }

            var names = element.EnumerateObject().Select(property => property.Name).ToList();
            if (names.Count != names.Distinct(StringComparer.Ordinal).Count())
            {
                Error(reference, "MDM001_DUPLICATE_PROPERTY");
                return false;
            }

            if (!names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                Error(reference, "MDM001_ENTRY_SHAPE");
                return false;
            }

            return true;
        }

        private static bool IsString(JsonElement parent, string property, out string value)
        {
            value = string.Empty;
            if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = element.GetString()!;
            return true;
        }

        private static bool IsValidName(string value) =>
            value.Length is >= 1 and <= 200 &&
            value == value.Trim() &&
            !value.Any(char.IsControl);

        private void Error(string reference, string code) => errors.Add($"{reference}: {code}");
    }

    [GeneratedRegex("^(0|[1-9][0-9]{0,17})$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerCents();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUuid();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryCode();

    [GeneratedRegex("^[A-Z0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex StateCode();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcInstant();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyVersion();
}

internal sealed record TariffWindow(string Key, DateTimeOffset From, DateTimeOffset? To, int Index, string Reference);

internal sealed record MasterDataValidationResult(
    IReadOnlyList<string> Errors,
    byte[] Sha256,
    IReadOnlyDictionary<string, int> SectionCounts,
    int DriverServiceAreaCount)
{
    internal bool IsValid => Errors.Count == 0;

    internal bool HasDriverProfiles =>
        SectionCounts.TryGetValue("driver_profiles", out var count) && count > 0;
}
