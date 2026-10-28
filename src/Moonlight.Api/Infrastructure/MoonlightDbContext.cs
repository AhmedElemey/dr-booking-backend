using Microsoft.EntityFrameworkCore;

namespace Moonlight.Api.Infrastructure;

public class MoonlightDbContext(DbContextOptions<MoonlightDbContext> options) : DbContext(options)
{
}
