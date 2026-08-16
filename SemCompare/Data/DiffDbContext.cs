using Microsoft.EntityFrameworkCore;
using SemCompare.Models;

namespace SemCompare.Data;

public class DiffDbContext : DbContext
{
    public DiffDbContext(DbContextOptions<DiffDbContext> options) : base(options) { }

    public DbSet<AppUser>      AppUsers      { get; set; }
    public DbSet<Repository>   Repositories  { get; set; }
    public DbSet<DiffRun>      DiffRuns      { get; set; }
    public DbSet<MethodChange> MethodChanges { get; set; }
    public DbSet<FieldChange>  FieldChanges  { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>()
         .HasIndex(u => u.GitHubId)
         .IsUnique();

        b.Entity<Repository>()
         .HasIndex(r => r.GitHubUrl);

        b.Entity<DiffRun>()
         .HasOne(r => r.Repository)
         .WithMany(r => r.Runs)
         .HasForeignKey(r => r.RepositoryId);

        b.Entity<DiffRun>()
         .HasOne(r => r.AppUser)
         .WithMany(u => u.Runs)
         .HasForeignKey(r => r.AppUserId)
         .IsRequired(false);

        b.Entity<MethodChange>()
         .HasOne(c => c.DiffRun)
         .WithMany(r => r.Changes)
         .HasForeignKey(c => c.DiffRunId);

        b.Entity<MethodChange>()
         .HasIndex(c => c.DiffRunId);

        b.Entity<MethodChange>()
         .HasIndex(c => new { c.ClassName, c.MethodName });

        b.Entity<MethodChange>()
         .HasIndex(c => c.IsBreaking);

        b.Entity<FieldChange>()
         .HasOne(f => f.DiffRun)
         .WithMany(r => r.FieldChanges)
         .HasForeignKey(f => f.DiffRunId);

        b.Entity<FieldChange>()
         .HasIndex(f => f.DiffRunId);

        b.Entity<FieldChange>()
         .HasIndex(f => new { f.ClassName, f.FieldName });

        b.Entity<FieldChange>()
         .HasIndex(f => f.IsBreaking);
    }
}
