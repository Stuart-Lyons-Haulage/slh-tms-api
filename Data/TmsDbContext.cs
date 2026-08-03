using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Data;
public sealed class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<StagedImport> StagedImports => Set<StagedImport>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<StagedImport>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<StagedImport>().Property(x => x.RowVersion).IsRowVersion();
    }
}
