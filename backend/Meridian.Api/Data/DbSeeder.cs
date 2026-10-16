using System.Globalization;
using Meridian.Domain;

namespace Meridian.Api.Data;

// Runs at startup. Idempotent: only inserts what's missing, never changes existing rows.
public static class DbSeeder
{
    public static readonly User[] Users =
    {
        new() { Id = "dana", Name = "Dana", Role = UserRole.Analyst },
        new() { Id = "omer", Name = "Omer", Role = UserRole.Analyst },
        new() { Id = "lena", Name = "Lena", Role = UserRole.Admin },
    };

    public static void Seed(MeridianDbContext db, string memberStatesCsvPath, ILogger logger)
    {
        var existingUsers = db.Users.Select(u => u.Id).ToHashSet();
        var newUsers = Users.Where(u => !existingUsers.Contains(u.Id)).ToList();
        db.Users.AddRange(newUsers);

        var existingPeriods = db.MemberStates
            .Select(m => new { m.CountryCode, m.MemberFrom })
            .AsEnumerable()
            .Select(m => (m.CountryCode, m.MemberFrom))
            .ToHashSet();
        var newPeriods = ReadMemberStates(memberStatesCsvPath)
            .Where(m => !existingPeriods.Contains((m.CountryCode, m.MemberFrom)))
            .ToList();
        db.MemberStates.AddRange(newPeriods);

        db.SaveChanges();
        logger.LogInformation("Seed: added {Users} users, {Periods} member state periods",
            newUsers.Count, newPeriods.Count);
    }

    // country_code,country_name,member_from,member_to
    // No quotes in this file; empty member_to = still a member.
    private static IEnumerable<MemberState> ReadMemberStates(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            if (f.Length != 4)
                throw new InvalidDataException($"member_states.csv: expected 4 columns, got {f.Length}: {line}");

            yield return new MemberState
            {
                CountryCode = f[0].Trim(),
                CountryName = f[1].Trim(),
                MemberFrom = ParseDate(f[2]),
                MemberTo = string.IsNullOrWhiteSpace(f[3]) ? null : ParseDate(f[3]),
            };
        }
    }

    private static DateOnly ParseDate(string s) =>
        DateOnly.ParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
