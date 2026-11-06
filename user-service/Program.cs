// user-service — гости. Пользователь создаётся автоматически при первом бронировании (по e-mail).
// Своя база данных: users_db.
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? "Host=db;Port=5432;Database=users_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<UserDb>(o => o.UseNpgsql(connectionString));

var app = builder.Build();

for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UserDb>().Database.EnsureCreatedAsync();
        break;
    }
    catch (Exception e) when (attempt < 30)
    {
        app.Logger.LogWarning("БД недоступна ({Msg}), попытка {N}", e.Message, attempt);
        await Task.Delay(2000);
    }
}

app.MapGet("/health", () => new { status = "Healthy", service = "user-service" });

app.MapGet("/users", async (UserDb db) => await db.Users.OrderBy(u => u.Id).ToListAsync());

app.MapGet("/users/{id:int}", async (int id, UserDb db) =>
    await db.Users.FindAsync(id) is User u ? Results.Ok(u) : Results.NotFound(new { error = "Пользователь не найден" }));

app.MapGet("/users/by-email", async (string email, UserDb db) =>
{
    var normalized = Normalize(email);
    return await db.Users.FirstOrDefaultAsync(u => u.Email == normalized) is User u
        ? Results.Ok(u) : Results.NotFound(new { error = "Пользователь не найден" });
});

// Найти пользователя по e-mail или создать нового. Вызывает booking-service.
app.MapPost("/users/resolve", async (ResolveRequest req, UserDb db) =>
{
    var email = Normalize(req.Email);
    var name = req.Name?.Trim() ?? "";
    if (name.Length < 2 || !email.Contains('@') || email.Length > 200)
        return Results.BadRequest(new { error = "Укажите имя и корректный e-mail" });

    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    var created = user is null;
    if (user is null)
    {
        user = new User { Name = name, Email = email, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
    }
    else
    {
        user.Name = name; // имя могло уточниться
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { user, created });
});

app.Run();

static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

record ResolveRequest(string? Name, string? Email);

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class UserDb : DbContext
{
    public UserDb(DbContextOptions<UserDb> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<User>().HasIndex(u => u.Email).IsUnique();
}
