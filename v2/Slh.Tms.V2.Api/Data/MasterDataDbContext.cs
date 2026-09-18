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
    public DbSet<FuelCard> FuelCards => Set<FuelCard>();
    public DbSet<Trailer> Trailers => Set<Trailer>();
    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();
    public DbSet<MarketContact> MarketContacts => Set<MarketContact>();
    public DbSet<SiteCutoff> SiteCutoffs => Set<SiteCutoff>();
    public DbSet<RouteTiming> RouteTimings => Set<RouteTiming>();
    public DbSet<FuelPrice> FuelPrices => Set<FuelPrice>();
    public DbSet<SiteAliasCandidate> SiteAliasCandidates => Set<SiteAliasCandidate>();
    public DbSet<MasterDataReviewItem> MasterDataReviewItems => Set<MasterDataReviewItem>();
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
        b.Entity<Site>().Property(x => x.DriverTextName).HasMaxLength(200);
        b.Entity<Site>().Property(x => x.Postcode).HasMaxLength(20);
        b.Entity<Site>().Property(x => x.DeadlineContact).HasMaxLength(200);
        b.Entity<Site>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<Site>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<Site>().HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Market>().HasKey(x => x.Id);
        b.Entity<Market>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Market>().Property(x => x.Code).HasMaxLength(80);
        b.Entity<Market>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<Market>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Driver>().HasKey(x => x.Id);
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique().HasFilter("[EmployeeNumber] IS NOT NULL");
        b.Entity<Driver>().Property(x => x.EmployeeNumber).HasMaxLength(80);
        b.Entity<Driver>().Property(x => x.DisplayName).HasMaxLength(200);
        b.Entity<Driver>().Property(x => x.MobileNumber).HasMaxLength(40);
        b.Entity<Driver>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<Driver>().Property(x => x.TachoMasterMemberCode).HasMaxLength(80);
        b.Entity<Driver>().Property(x => x.TachoMasterDriverId).HasMaxLength(120);
        b.Entity<Driver>().Property(x => x.TachoCardNumber).HasMaxLength(120);

        b.Entity<Vehicle>().HasKey(x => x.Id);
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Vehicle>().Property(x => x.Registration).HasMaxLength(40);
        b.Entity<Vehicle>().Property(x => x.FleetNumber).HasMaxLength(80);

        b.Entity<FuelCard>().HasKey(x => x.Id);
        b.Entity<FuelCard>().HasIndex(x => new { x.Provider, x.CardType, x.CardNumber }).IsUnique();
        b.Entity<FuelCard>().Property(x => x.Provider).HasMaxLength(80);
        b.Entity<FuelCard>().Property(x => x.CardType).HasMaxLength(80);
        b.Entity<FuelCard>().Property(x => x.CardNumber).HasMaxLength(120);
        b.Entity<FuelCard>().Property(x => x.Pin).HasMaxLength(40);
        b.Entity<FuelCard>().HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<Trailer>().HasKey(x => x.Id);
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();
        b.Entity<Trailer>().Property(x => x.TrailerNumber).HasMaxLength(80);
        b.Entity<Trailer>().Property(x => x.Registration).HasMaxLength(40);
        b.Entity<Trailer>().Property(x => x.TrailerType).HasMaxLength(80);
        b.Entity<Trailer>().Property(x => x.CurrentLocation).HasMaxLength(160);

        b.Entity<CustomerContact>().HasKey(x => x.Id);
        b.Entity<CustomerContact>().HasIndex(x => x.Code).IsUnique();
        b.Entity<CustomerContact>().Property(x => x.Code).HasMaxLength(80);
        b.Entity<CustomerContact>().HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomerContact>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<MarketContact>().HasKey(x => x.Id);
        b.Entity<MarketContact>().HasIndex(x => x.Key).IsUnique();
        b.Entity<MarketContact>().Property(x => x.Key).HasMaxLength(300);
        b.Entity<MarketContact>().Property(x => x.MarketName).HasMaxLength(120);
        b.Entity<MarketContact>().Property(x => x.Name).HasMaxLength(200);

        b.Entity<SiteCutoff>().HasKey(x => x.Id);
        b.Entity<SiteCutoff>().HasIndex(x => x.Code).IsUnique();
        b.Entity<SiteCutoff>().Property(x => x.Code).HasMaxLength(80);
        b.Entity<SiteCutoff>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<RouteTiming>().HasKey(x => x.Id);
        b.Entity<RouteTiming>().HasIndex(x => x.Key).IsUnique();
        b.Entity<RouteTiming>().Property(x => x.Key).HasMaxLength(300);
        b.Entity<RouteTiming>().Property(x => x.Route).HasMaxLength(250);
        b.Entity<RouteTiming>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<FuelPrice>().HasKey(x => x.Id);
        b.Entity<FuelPrice>().HasIndex(x => x.Code).IsUnique();
        b.Entity<FuelPrice>().Property(x => x.Code).HasMaxLength(80);
        b.Entity<FuelPrice>().Property(x => x.Provider).HasMaxLength(120);
        b.Entity<FuelPrice>().Property(x => x.PricePencePerLitre).HasPrecision(10, 4);

        b.Entity<SiteAliasCandidate>().HasKey(x => x.Id);
        b.Entity<SiteAliasCandidate>()
            .HasIndex(x => new { x.AliasType, x.Alias })
            .IsUnique();
        b.Entity<SiteAliasCandidate>().Property(x => x.AliasType).HasMaxLength(40);
        b.Entity<SiteAliasCandidate>().Property(x => x.Alias).HasMaxLength(250);
        b.Entity<SiteAliasCandidate>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<MasterDataReviewItem>().HasKey(x => x.Id);
        b.Entity<MasterDataReviewItem>().HasIndex(x => x.Key).IsUnique();
        b.Entity<MasterDataReviewItem>().HasIndex(x => new { x.Resolved, x.Category, x.CreatedAtUtc });
        b.Entity<MasterDataReviewItem>().Property(x => x.Key).HasMaxLength(300);
        b.Entity<MasterDataReviewItem>().Property(x => x.Category).HasMaxLength(80);
        b.Entity<MasterDataReviewItem>().Property(x => x.EntityType).HasMaxLength(80);
        b.Entity<MasterDataReviewItem>().Property(x => x.SourceReference).HasMaxLength(160);

        b.Entity<ExternalIdentity>().HasKey(x => x.Id);
        b.Entity<ExternalIdentity>().Property(x => x.Provider).HasMaxLength(80);
        b.Entity<ExternalIdentity>().Property(x => x.EntityType).HasMaxLength(80);
        b.Entity<ExternalIdentity>().Property(x => x.ExternalKey).HasMaxLength(200);
        b.Entity<ExternalIdentity>()
            .HasIndex(x => new { x.Provider, x.EntityType, x.ExternalKey })
            .IsUnique()
            .HasFilter("[Active] = 1");

        b.Entity<SiteAlias>().HasKey(x => x.Id);
        b.Entity<SiteAlias>().HasIndex(x => new { x.SiteId, x.Alias }).IsUnique();
        b.Entity<SiteAlias>().Property(x => x.Alias).HasMaxLength(250);
        b.Entity<SiteAlias>().HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
    }
}
