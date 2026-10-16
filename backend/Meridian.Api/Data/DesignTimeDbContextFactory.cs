using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Meridian.Api.Data;

// Used only by `dotnet ef migrations add`: builds the model without starting the app.
// No connection is opened, so the connection string is a dummy.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MeridianDbContext>
{
    public MeridianDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MeridianDbContext>()
            .UseNpgsql("Host=design-time-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new MeridianDbContext(options);
    }
}
