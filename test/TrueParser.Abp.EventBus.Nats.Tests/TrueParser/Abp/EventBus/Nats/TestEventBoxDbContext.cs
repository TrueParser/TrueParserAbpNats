using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.DependencyInjection;

namespace TrueParser.Abp.EventBus.Nats;

public class TestEventBoxDbContext : AbpDbContext<TestEventBoxDbContext>, IHasEventOutbox, IHasEventInbox
{
    public DbSet<OutgoingEventRecord> OutgoingEvents { get; set; } = null!;

    public DbSet<IncomingEventRecord> IncomingEvents { get; set; } = null!;

    public TestEventBoxDbContext(
        DbContextOptions<TestEventBoxDbContext> options,
        IAbpLazyServiceProvider lazyServiceProvider)
        : base(options)
    {
        LazyServiceProvider = lazyServiceProvider;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureEventOutbox();
        modelBuilder.ConfigureEventInbox();
    }
}
