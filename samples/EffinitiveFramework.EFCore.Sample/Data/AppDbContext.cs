using EffinitiveFramework.EFCore.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace EffinitiveFramework.EFCore.Sample.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Product configuration
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Category);
        });

        // Order configuration
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CustomerEmail).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CustomerEmail);
        });

        // OrderItem configuration
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.Subtotal).HasPrecision(18, 2);
            
            entity.HasOne(e => e.Order)
                .WithMany(o => o.Items)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed data
        modelBuilder.Entity<Product>().HasData(
            new Product
            {
                Id = 1,
                Name = "Laptop Pro 15",
                Description = "High-performance laptop with 16GB RAM and 512GB SSD",
                Price = 1299.99m,
                Stock = 50,
                Category = "Electronics",
                CreatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = 2,
                Name = "Wireless Mouse",
                Description = "Ergonomic wireless mouse with precision tracking",
                Price = 29.99m,
                Stock = 200,
                Category = "Accessories",
                CreatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = 3,
                Name = "USB-C Hub",
                Description = "7-in-1 USB-C hub with HDMI, USB 3.0, and SD card reader",
                Price = 49.99m,
                Stock = 150,
                Category = "Accessories",
                CreatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = 4,
                Name = "Mechanical Keyboard",
                Description = "RGB mechanical keyboard with Cherry MX switches",
                Price = 149.99m,
                Stock = 75,
                Category = "Accessories",
                CreatedAt = DateTime.UtcNow
            },
            new Product
            {
                Id = 5,
                Name = "4K Monitor",
                Description = "27-inch 4K IPS monitor with HDR support",
                Price = 399.99m,
                Stock = 30,
                Category = "Electronics",
                CreatedAt = DateTime.UtcNow
            }
        );
    }
}
