using Meridian.Domain;
using Meridian.Domain.Ingestion;

namespace Meridian.Tests.Domain;

// Small member list covering the interesting cases from member_states.csv
public static class TestData
{
    public static readonly MemberList Members = new(new[]
    {
        new MembershipPeriod("BRA", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("IND", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("JPN", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("FRA", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("DEU", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("TUR", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("ARG", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("AGO", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("EST", new DateOnly(1995, 1, 1), null),
        new MembershipPeriod("NZL", new DateOnly(1995, 1, 1), new DateOnly(2021, 6, 30)),
        new MembershipPeriod("ISL", new DateOnly(1995, 1, 1), new DateOnly(2019, 12, 31)),
        new MembershipPeriod("ISL", new DateOnly(2022, 1, 1), null),
    });

    public const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    // One CSV line with sensible defaults
    public static string Line(string vote, string country, string ballot,
        string title = "Test Resolution", string date = "2024-01-12", string session = "79",
        string? name = null, string publishedAt = "2024-04-02T09:00:00Z") =>
        $"{vote},{session},{date},{title},{country},{name ?? country + " name"},{ballot},{publishedAt}";

    // A valid row, built directly (for classifier tests)
    public static VoteRow Row(int line, string country, Ballot ballot,
        string vote = "MA/2024/001", string title = "Test Resolution", int session = 79,
        DateOnly? date = null, string? name = null, string publishedAt = "2024-04-02T09:00:00Z") =>
        new(line, vote, session, date ?? new DateOnly(2024, 1, 12), title, country, name ?? country + " name", ballot, publishedAt);

    public static ApprovedVote Approved(Dictionary<string, Ballot> ballots, string title = "Test Resolution",
        int session = 79, DateOnly? date = null, params ExistingConflict[] conflicts) =>
        new(session, date ?? new DateOnly(2024, 1, 12), title, ballots, conflicts);
}
