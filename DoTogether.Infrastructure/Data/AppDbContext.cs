using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Household> Households => Set<Household>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();
    public DbSet<ChoreTemplate> ChoreTemplates => Set<ChoreTemplate>();
    public DbSet<ChoreOccurrence> ChoreOccurrences => Set<ChoreOccurrence>();
    public DbSet<ChoreEvent> ChoreEvents => Set<ChoreEvent>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<DevicePushToken> DevicePushTokens => Set<DevicePushToken>();
    public DbSet<IdempotentOperation> IdempotentOperations => Set<IdempotentOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── User ──
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(120);
            e.Property(u => u.PasswordHash).HasMaxLength(512);
            e.HasQueryFilter(u => !u.IsDeleted);
        });

        // ── Household ──
        modelBuilder.Entity<Household>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.Name).HasMaxLength(120);
            e.Property(h => h.TimeZoneId).HasMaxLength(100);
            e.HasQueryFilter(h => !h.IsDeleted);
        });

        // ── HouseholdMember ──
        modelBuilder.Entity<HouseholdMember>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.HouseholdId, m.UserId }).IsUnique();
            e.HasOne(m => m.Household).WithMany(h => h.Members).HasForeignKey(m => m.HouseholdId);
            e.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId);
            e.HasQueryFilter(m => !m.IsDeleted);
        });

        // ── HouseholdInvite ──
        modelBuilder.Entity<HouseholdInvite>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.Token).IsUnique();
            e.Property(i => i.InviteeEmail).HasMaxLength(256);
            e.Property(i => i.Token).HasMaxLength(64);
            e.HasOne(i => i.Household).WithMany(h => h.Invites).HasForeignKey(i => i.HouseholdId);
            e.HasQueryFilter(i => !i.IsDeleted);
        });

        // ── ChoreTemplate ──
        modelBuilder.Entity<ChoreTemplate>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).HasMaxLength(200);
            e.HasIndex(t => new { t.HouseholdId, t.IsActive });
            e.HasOne(t => t.Household).WithMany(h => h.ChoreTemplates).HasForeignKey(t => t.HouseholdId);
            e.HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.CreatedByUser).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(t => !t.IsDeleted);

            // RecurrenceRule as owned JSON column.
            e.OwnsOne(t => t.RecurrenceRule, r =>
            {
                r.ToJson();
            });
        });

        // ── ChoreOccurrence ──
        modelBuilder.Entity<ChoreOccurrence>(e =>
        {
            e.HasKey(o => o.Id);
            // Primary calendar query index.
            e.HasIndex(o => new { o.HouseholdId, o.DueDate, o.Status });
            // For recurrence generation dedup.
            e.HasIndex(o => new { o.ChoreTemplateId, o.DueDate });
            // Achievement scope queries (filter by household + status + optional assignee).
            e.HasIndex(o => new { o.HouseholdId, o.Status, o.AssigneeId });
            e.HasOne(o => o.ChoreTemplate).WithMany(t => t.Occurrences).HasForeignKey(o => o.ChoreTemplateId);
            e.HasOne(o => o.Household).WithMany().HasForeignKey(o => o.HouseholdId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Assignee).WithMany().HasForeignKey(o => o.AssigneeId).OnDelete(DeleteBehavior.SetNull);
            e.Property(o => o.Version).IsConcurrencyToken();
            e.HasQueryFilter(o => !o.IsDeleted);
        });

        // ── ChoreEvent ──
        modelBuilder.Entity<ChoreEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => ev.ClientOperationId).IsUnique();
            e.HasIndex(ev => new { ev.ChoreOccurrenceId, ev.OccurredAtUtc });
            e.HasOne(ev => ev.ChoreOccurrence).WithMany(o => o.Events).HasForeignKey(ev => ev.ChoreOccurrenceId);
            e.HasOne(ev => ev.PerformedByUser).WithMany().HasForeignKey(ev => ev.PerformedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(ev => !ev.IsDeleted);
        });

        // ── RefreshToken ──
        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Token).IsUnique();
            e.Property(r => r.Token).HasMaxLength(256);
            e.HasOne(r => r.User).WithMany(u => u.RefreshTokens).HasForeignKey(r => r.UserId);
        });

        // ── DevicePushToken ──
        modelBuilder.Entity<DevicePushToken>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.UserId, d.Platform, d.Token }).IsUnique();
            e.Property(d => d.Token).HasMaxLength(512);
            e.HasOne(d => d.User).WithMany(u => u.DevicePushTokens).HasForeignKey(d => d.UserId);
        });

        // ── IdempotentOperation ──
        modelBuilder.Entity<IdempotentOperation>(e =>
        {
            e.HasKey(o => o.ClientOperationId);
            e.HasIndex(o => o.HouseholdId);
            e.Property(o => o.OperationType).HasMaxLength(50);
        });
    }
}
