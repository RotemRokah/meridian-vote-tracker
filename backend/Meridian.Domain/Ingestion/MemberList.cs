namespace Meridian.Domain.Ingestion;

public record MembershipPeriod(string CountryCode, DateOnly From, DateOnly? To);

// Member states with their membership periods. A country can have several (ISL left and rejoined).
public class MemberList
{
    private readonly Dictionary<string, List<MembershipPeriod>> _periods;

    public MemberList(IEnumerable<MembershipPeriod> periods)
    {
        _periods = periods
            .GroupBy(p => p.CountryCode)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public bool IsKnown(string countryCode) => _periods.ContainsKey(countryCode);

    // Both ends inclusive: member_to is the last day of membership
    public bool WasMemberOn(string countryCode, DateOnly date) =>
        _periods.TryGetValue(countryCode, out var list)
        && list.Any(p => p.From <= date && (p.To == null || date <= p.To));
}
