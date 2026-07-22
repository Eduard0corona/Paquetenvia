namespace Locations.Domain;

public sealed class City
{
    private City()
    {
    }

    public City(Guid id, string countryCode, string stateCode, string name, string timezone, GeographicStatus status)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(stateCode) ||
            string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(timezone) || !Enum.IsDefined(status))
        {
            throw new ArgumentException("The city is invalid.");
        }

        Id = id;
        CountryCode = countryCode;
        StateCode = stateCode;
        Name = name;
        Timezone = timezone;
        Status = status;
    }

    public Guid Id { get; private set; }
    public string CountryCode { get; private set; } = string.Empty;
    public string StateCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Timezone { get; private set; } = string.Empty;
    public GeographicStatus Status { get; private set; }
}
