// notification-service — журнал уведомлений гостям (подтверждение и отмена брони).
// Своя база данных: notifications_db. Реальные письма не отправляются — только сохраняются.
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? "Host=db;Port=5432;Database=notifications_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<NotificationDb>(o => o.UseNpgsql(connectionString));

var app = builder.Build();

for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationDb>().Database.EnsureCreatedAsync();
        break;
    }
    catch (Exception e) when (attempt < 30)
    {
        app.Logger.LogWarning("БД недоступна ({Msg}), попытка {N}", e.Message, attempt);
        await Task.Delay(2000);
    }
}

app.MapGet("/health", () => new { status = "Healthy", service = "notification-service" });

// Все уведомления или только для одного e-mail.
app.MapGet("/notifications", async (NotificationDb db, string? email) =>
{
    var q = db.Notifications.AsQueryable();
    if (!string.IsNullOrWhiteSpace(email)) q = q.Where(n => n.Email == email.Trim().ToLower());
    return await q.OrderByDescending(n => n.Id).Take(100).ToListAsync();
});

// Принять уведомление. Вызывает booking-service.
app.MapPost("/notifications", async (NotificationRequest req, NotificationDb db) =>
{
    var n = new Notification
    {
        BookingId = req.BookingId,
        UserId = req.UserId,
        Email = (req.Email ?? "").Trim().ToLower(),
        Type = req.Type ?? "Info",
        Message = req.Message ?? "",
        CreatedAt = DateTime.UtcNow
    };
    db.Notifications.Add(n);
    await db.SaveChangesAsync();
    app.Logger.LogInformation("→ {Email}: {Message}", n.Email, n.Message);
    return Results.Created($"/notifications/{n.Id}", n);
});

app.Run();

record NotificationRequest(int BookingId, int UserId, string? Email, string? Type, string? Message);

public class Notification
{
    public int Id { get; set; }
    public int BookingId { get; set; }
    public int UserId { get; set; }
    public string Email { get; set; } = "";
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class NotificationDb : DbContext
{
    public NotificationDb(DbContextOptions<NotificationDb> options) : base(options) { }
    public DbSet<Notification> Notifications => Set<Notification>();
}
