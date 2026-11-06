// booking-service — бронирования. Главный сервис сценария:
//   1) спрашивает hotel-service про номер (цена, вместимость),
//   2) проверяет, что даты не пересекаются с другими бронями,
//   3) находит/создаёт гостя в user-service,
//   4) сохраняет бронь в свою базу bookings_db,
//   5) отправляет уведомление в notification-service.
using System.Net;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? "Host=db;Port=5432;Database=bookings_db;Username=postgres;Password=postgres";
var hotelUrl = Environment.GetEnvironmentVariable("HOTEL_SERVICE_URL") ?? "http://hotel-service:8083";
var userUrl = Environment.GetEnvironmentVariable("USER_SERVICE_URL") ?? "http://user-service:8081";
var notifyUrl = Environment.GetEnvironmentVariable("NOTIFICATION_SERVICE_URL") ?? "http://notification-service:8082";

builder.Services.AddDbContext<BookingDb>(o => o.UseNpgsql(connectionString));
builder.Services.AddHttpClient("hotel", c => { c.BaseAddress = new Uri(hotelUrl); c.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddHttpClient("user", c => { c.BaseAddress = new Uri(userUrl); c.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddHttpClient("notify", c => { c.BaseAddress = new Uri(notifyUrl); c.Timeout = TimeSpan.FromSeconds(5); });

var app = builder.Build();

for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BookingDb>().Database.EnsureCreatedAsync();
        break;
    }
    catch (Exception e) when (attempt < 30)
    {
        app.Logger.LogWarning("БД недоступна ({Msg}), попытка {N}", e.Message, attempt);
        await Task.Delay(2000);
    }
}

app.MapGet("/health", () => new { status = "Healthy", service = "booking-service" });

// Все брони или брони одного гостя (?email=).
app.MapGet("/bookings", async (BookingDb db, string? email) =>
{
    var q = db.Bookings.AsQueryable();
    if (!string.IsNullOrWhiteSpace(email)) q = q.Where(b => b.Email == email.Trim().ToLower());
    return await q.OrderByDescending(b => b.Id).ToListAsync();
});

app.MapGet("/bookings/{id:int}", async (int id, BookingDb db) =>
    await db.Bookings.FindAsync(id) is Booking b ? Results.Ok(b) : Results.NotFound(new { error = "Бронь не найдена" }));

// Какие номера отеля заняты на выбранные даты.
app.MapGet("/availability", async (int hotelId, DateOnly checkIn, DateOnly checkOut, BookingDb db) =>
{
    if (checkOut <= checkIn) return Results.BadRequest(new { error = "Дата выезда должна быть позже даты заезда" });
    var busy = await db.Bookings
        .Where(b => b.HotelId == hotelId && b.Status == "Confirmed" && b.CheckIn < checkOut && checkIn < b.CheckOut)
        .Select(b => b.RoomId).Distinct().ToListAsync();
    return Results.Ok(new { hotelId, checkIn, checkOut, busyRoomIds = busy });
});

// Создать бронь — основной сценарий.
app.MapPost("/bookings", async (CreateBooking req, BookingDb db, IHttpClientFactory http) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    if (req.CheckOut <= req.CheckIn) return Error(400, "Дата выезда должна быть позже даты заезда");
    if (req.CheckIn < today) return Error(400, "Дата заезда уже прошла");
    if (req.CheckOut.DayNumber - req.CheckIn.DayNumber > 30) return Error(400, "Не больше 30 ночей");
    if (req.Guests < 1) return Error(400, "Минимум один гость");

    // 1. Номер — у hotel-service.
    RoomInfo? room;
    try
    {
        var resp = await http.CreateClient("hotel").GetAsync($"/rooms/{req.RoomId}");
        if (resp.StatusCode == HttpStatusCode.NotFound) return Error(404, "Номер не найден");
        resp.EnsureSuccessStatusCode();
        room = await resp.Content.ReadFromJsonAsync<RoomInfo>();
    }
    catch (HttpRequestException) { return Error(503, "hotel-service недоступен"); }
    if (room is null) return Error(502, "Пустой ответ hotel-service");
    if (req.Guests > room.Capacity) return Error(400, $"В номере «{room.Type}» помещается до {room.Capacity} гостей");

    // 2. Гость — у user-service (создаётся, если новый).
    ResolvedUser? resolved;
    try
    {
        var resp = await http.CreateClient("user").PostAsJsonAsync("/users/resolve", new { req.Name, req.Email });
        if (resp.StatusCode == HttpStatusCode.BadRequest) return Error(400, "Укажите имя и корректный e-mail");
        resp.EnsureSuccessStatusCode();
        resolved = await resp.Content.ReadFromJsonAsync<ResolvedUser>();
    }
    catch (HttpRequestException) { return Error(503, "user-service недоступен"); }
    if (resolved is null) return Error(502, "Пустой ответ user-service");

    // 3. Проверка пересечения дат и сохранение — в одной транзакции с блокировкой по номеру,
    //    чтобы два человека одновременно не забронировали один номер.
    var nights = req.CheckOut.DayNumber - req.CheckIn.DayNumber;
    Booking booking;
    await using (var tx = await db.Database.BeginTransactionAsync())
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({room.Id})");
        var overlap = await db.Bookings.AnyAsync(b => b.RoomId == room.Id && b.Status == "Confirmed"
            && b.CheckIn < req.CheckOut && req.CheckIn < b.CheckOut);
        if (overlap) return Error(409, "Номер уже занят на эти даты");

        booking = new Booking
        {
            HotelId = room.HotelId, HotelName = room.HotelName, City = room.City,
            RoomId = room.Id, RoomType = room.Type,
            UserId = resolved.User.Id, GuestName = resolved.User.Name, Email = resolved.User.Email,
            CheckIn = req.CheckIn, CheckOut = req.CheckOut, Guests = req.Guests,
            Nights = nights, TotalPrice = room.Price * nights,
            Status = "Confirmed", CreatedAt = DateTime.UtcNow
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // 4. Уведомление. Если сервис уведомлений лежит — бронь всё равно остаётся.
    booking.Notified = await Notify(http, booking, "BookingConfirmed",
        $"Бронь №{booking.Id} подтверждена: {booking.HotelName}, {booking.RoomType}, {booking.CheckIn:dd.MM} — {booking.CheckOut:dd.MM}, {booking.TotalPrice:0} ₽");
    await db.SaveChangesAsync();

    return Results.Created($"/bookings/{booking.Id}", booking);
});

// Отменить бронь.
app.MapPost("/bookings/{id:int}/cancel", async (int id, BookingDb db, IHttpClientFactory http) =>
{
    var booking = await db.Bookings.FindAsync(id);
    if (booking is null) return Error(404, "Бронь не найдена");
    if (booking.Status == "Cancelled") return Error(409, "Бронь уже отменена");
    booking.Status = "Cancelled";
    await db.SaveChangesAsync();
    await Notify(http, booking, "BookingCancelled", $"Бронь №{booking.Id} в «{booking.HotelName}» отменена");
    return Results.Ok(booking);
});

app.Run();

static IResult Error(int code, string message) => Results.Json(new { error = message }, statusCode: code);

static async Task<bool> Notify(IHttpClientFactory http, Booking b, string type, string message)
{
    try
    {
        var resp = await http.CreateClient("notify").PostAsJsonAsync("/notifications",
            new { BookingId = b.Id, b.UserId, b.Email, Type = type, Message = message });
        return resp.IsSuccessStatusCode;
    }
    catch (HttpRequestException) { return false; }
}

record CreateBooking(int RoomId, DateOnly CheckIn, DateOnly CheckOut, int Guests, string? Name, string? Email);
record RoomInfo(int Id, int HotelId, string HotelName, string City, string Type, int Capacity, decimal Price);
record UserInfo(int Id, string Name, string Email);
record ResolvedUser(UserInfo User, bool Created);

public class Booking
{
    public int Id { get; set; }
    public int HotelId { get; set; }
    public string HotelName { get; set; } = "";
    public string City { get; set; } = "";
    public int RoomId { get; set; }
    public string RoomType { get; set; } = "";
    public int UserId { get; set; }
    public string GuestName { get; set; } = "";
    public string Email { get; set; } = "";
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public int Guests { get; set; }
    public int Nights { get; set; }
    public decimal TotalPrice { get; set; }
    public string Status { get; set; } = "Confirmed";
    public bool Notified { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BookingDb : DbContext
{
    public BookingDb(DbContextOptions<BookingDb> options) : base(options) { }
    public DbSet<Booking> Bookings => Set<Booking>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Booking>().HasIndex(x => new { x.RoomId, x.CheckIn });
}
