using ChatRoom2.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatRoom2.Data;

public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<PlaylistItem> Playlists => Set<PlaylistItem>();
    public DbSet<BannedIp> BannedIps => Set<BannedIp>();
    public DbSet<MuteRecord> MuteRecords => Set<MuteRecord>();
public DbSet<Announcement> Announcements => Set<Announcement>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<Message>()
            .HasOne(m => m.User)
            .WithMany(u => u.Messages)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}