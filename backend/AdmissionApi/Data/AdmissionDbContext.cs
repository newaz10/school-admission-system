using AdmissionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdmissionApi.Data;

public class AdmissionDbContext : DbContext
{
    public AdmissionDbContext(DbContextOptions<AdmissionDbContext> options)
        : base(options)
    {
    }

    public DbSet<ClassRoom> Classes => Set<ClassRoom>();
    public DbSet<Hold> Holds => Set<Hold>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClassRoom>(entity =>
        {
            entity.ToTable("classes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Hold>(entity =>
        {
            entity.ToTable("holds");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.StudentId).HasMaxLength(32).IsRequired();

            // Store the enum as readable text ("HELD", "CONFIRMED", ...) in PostgreSQL.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Class)
                  .WithMany(c => c.Holds)
                  .HasForeignKey(e => e.ClassId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Speeds up seat counting and the per-student duplicate check.
            entity.HasIndex(e => new { e.ClassId, e.Status });
            entity.HasIndex(e => new { e.StudentId, e.ClassId });
        });
    }
}