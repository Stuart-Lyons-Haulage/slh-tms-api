using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Data;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<TransportOrder> Orders => Set<TransportOrder>();
    public DbSet<OrderSourceLink> OrderSourceLinks => Set<OrderSourceLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("ops");

        b.Entity<TransportOrder>().HasKey(x => x.Id);
        b.Entity<TransportOrder>().HasIndex(x => x.StableKey).IsUnique();
        b.Entity<TransportOrder>().HasIndex(x => new { x.CollectionDate, x.State });
        b.Entity<TransportOrder>().HasIndex(x => x.CustomerId);
        b.Entity<TransportOrder>().HasIndex(x => x.CollectionSiteId);
        b.Entity<TransportOrder>().HasIndex(x => x.DeliverySiteId);
        b.Entity<TransportOrder>().Property(x => x.StableKey).HasMaxLength(250);
        b.Entity<TransportOrder>().Property(x => x.PurchaseOrder).HasMaxLength(120);
        b.Entity<TransportOrder>().Property(x => x.CustomerOrderReference).HasMaxLength(160);
        b.Entity<TransportOrder>().Property(x => x.SourceOrderReference).HasMaxLength(160);

        b.Entity<OrderSourceLink>().HasKey(x => x.Id);
        b.Entity<OrderSourceLink>().HasIndex(x => new { x.OrderId, x.RevisionNumber }).IsUnique();
        b.Entity<OrderSourceLink>().HasIndex(x => x.EvidenceId);
        b.Entity<OrderSourceLink>().HasOne<TransportOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
