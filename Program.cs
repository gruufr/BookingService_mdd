using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING") 
    ?? "Host=db;Port=5432;Database=app;Username=postgres;Password=1234";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpClient();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => new { status = "Healthy", service = "booking-service" });

app.MapGet("/bookings", async (AppDbContext db) =>
    await db.Bookings.ToListAsync());

app.MapGet("/bookings/{id:int}", async (int id, AppDbContext db) =>
    await db.Bookings.FindAsync(id) is Booking booking ? Results.Ok(booking) : Results.NotFound());

app.MapPost("/bookings", async (Booking booking, AppDbContext db, IHttpClientFactory clientFactory) =>
{
    db.Bookings.Add(booking);
    await db.SaveChangesAsync();

    try
    {
        var client = clientFactory.CreateClient();
        await client.PostAsJsonAsync("http://notification-service:8082/notifications", new 
        { 
            BookingId = booking.Id, 
            CustomerName = booking.CustomerName 
        });
    }
    catch
    {
    }

    return Results.Created($"/bookings/{booking.Id}", booking);
});

app.MapPut("/bookings/{id:int}", async (int id, Booking inputBooking, AppDbContext db) =>
{
    var booking = await db.Bookings.FindAsync(id);
    if (booking is null) return Results.NotFound();

    booking.CustomerName = inputBooking.CustomerName;
    booking.RoomType = inputBooking.RoomType;
    booking.Days = inputBooking.Days;

    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

public class Booking
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public int Days { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Booking> Bookings => Set<Booking>();
}
