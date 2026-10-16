using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Data;

// Display name per country code: the latest membership period's name (the code is the identity, ING-11)
public static class CountryNames
{
    public static async Task<Dictionary<string, string>> LoadAsync(MeridianDbContext db, CancellationToken ct) =>
        (await db.MemberStates.AsNoTracking().ToListAsync(ct))
            .GroupBy(m => m.CountryCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MemberFrom).First().CountryName);
}
