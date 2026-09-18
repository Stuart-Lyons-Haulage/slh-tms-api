using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Data;

public sealed class MasterDataDbContext(DbContextOptions<MasterDataDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Market> Markets => Set<Market>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Trailer> Trailers => Set<Trailer>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<SiteAlias> SiteAliases => Set<SiteAlias>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("master");

        b.Entity<Customer>().HasKey(x => x.Id);
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Customer>().Property(x => x.Code).HasMaxLength(40);
        b.Entity<Customer>().Property(x => x.Name).HasMaxLength(200);

        b.Entity<Site>().HasKey(x => x.Id);
        b.Entity<Site>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Site>().Property(x => x.Code).HasMaxLength(80);
        b.Entity<Site>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<Site>().Property(x => x.Postcode).HasMaxLength(20);
        b.Entity<Site>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<Site>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<Site>().HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Market>().HasKey(x => x.Id);
        b.Entity<Market>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Market>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Driver>().HasKey(x => x.Id);
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique().HasFilter("[EmployeeNumber] IS NOT NULL");

        b.Entity<Vehicle>().HasKey(x => x.Id);
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();

        b.Entity<Trailer>().HasKey(x => x.Id);
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();

        b.Entity<ExternalIdentity>().HasKey(x => x.Id);
        b.Entity<ExternalIdentity>()
            .HasIndex(x => new { x.Provider, x.EntityType, x.ExternalKey })
            .IsUnique()
            .HasFilter("[Active] = 1");

        b.Entity<SiteAlias>().HasKey(x => x.Id);
        b.Entity<SiteAlias>().HasIndex(x => new { x.SiteId, x.Alias }).IsUnique();
        b.Entity<SiteAlias>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
    }
}
