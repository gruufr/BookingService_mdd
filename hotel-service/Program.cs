// hotel-service — каталог отелей: отели, номера, отзывы гостей.
// Своя база данных: hotels_db.
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? "Host=db;Port=5432;Database=hotels_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<HotelDb>(o => o.UseNpgsql(connectionString));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

await Db.InitAsync(app.Services, app.Logger);

app.MapGet("/health", () => new { status = "Healthy", service = "hotel-service" });

// Список отелей с фильтрами: город, минимум звёзд, максимальная цена, сортировка.
app.MapGet("/hotels", async (HotelDb db, string? city, int? minStars, decimal? maxPrice, string? sort) =>
{
    var q = db.Hotels.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(city)) q = q.Where(h => h.City == city);
    if (minStars is not null) q = q.Where(h => h.Stars >= minStars);
    if (maxPrice is not null) q = q.Where(h => h.PricePerNight <= maxPrice);
    q = sort switch
    {
        "price" => q.OrderBy(h => h.PricePerNight),
        "-price" => q.OrderByDescending(h => h.PricePerNight),
        "stars" => q.OrderByDescending(h => h.Stars).ThenByDescending(h => h.Rating),
        _ => q.OrderByDescending(h => h.Rating)
    };
    return await q.ToListAsync();
});

app.MapGet("/hotels/cities", async (HotelDb db) =>
    await db.Hotels.Select(h => h.City).Distinct().OrderBy(c => c).ToListAsync());

// Карточка отеля: сам отель, его номера и отзывы.
app.MapGet("/hotels/{id:int}", async (int id, HotelDb db) =>
{
    var hotel = await db.Hotels.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id);
    if (hotel is null) return Results.NotFound(new { error = "Отель не найден" });
    var rooms = await db.Rooms.AsNoTracking().Where(r => r.HotelId == id).OrderBy(r => r.Price).ToListAsync();
    var reviews = await db.Reviews.AsNoTracking().Where(r => r.HotelId == id).OrderByDescending(r => r.Date).ToListAsync();
    return Results.Ok(new { hotel, rooms, reviews });
});

// Добавить отель (для демонстрации CRUD через curl/Postman).
app.MapPost("/hotels", async (Hotel input, HotelDb db) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.City))
        return Results.BadRequest(new { error = "Нужны name и city" });
    input.Id = 0;
    input.Rating = 0;
    input.ReviewsCount = 0;
    db.Hotels.Add(input);
    await db.SaveChangesAsync();
    db.Rooms.Add(new Room { HotelId = input.Id, Type = "Стандарт", Capacity = 2, Price = input.PricePerNight });
    await db.SaveChangesAsync();
    return Results.Created($"/hotels/{input.Id}", input);
});

// Номер по id — этим пользуется booking-service, чтобы узнать цену и вместимость.
app.MapGet("/rooms/{id:int}", async (int id, HotelDb db) =>
{
    var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    if (room is null) return Results.NotFound(new { error = "Номер не найден" });
    var hotel = await db.Hotels.AsNoTracking().FirstAsync(h => h.Id == room.HotelId);
    return Results.Ok(new { room.Id, room.HotelId, HotelName = hotel.Name, hotel.City, room.Type, room.Capacity, room.Price });
});

app.Run();

public class Hotel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";
    public int Stars { get; set; }
    public decimal PricePerNight { get; set; }
    public double Rating { get; set; }
    public int ReviewsCount { get; set; }
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public List<string> Amenities { get; set; } = new();
}

public class Room
{
    public int Id { get; set; }
    public int HotelId { get; set; }
    public string Type { get; set; } = "";
    public int Capacity { get; set; }
    public decimal Price { get; set; }
}

public class Review
{
    public int Id { get; set; }
    public int HotelId { get; set; }
    public string Author { get; set; } = "";
    public double Score { get; set; }
    public string Text { get; set; } = "";
    public DateOnly Date { get; set; }
}

public class HotelDb : DbContext
{
    public HotelDb(DbContextOptions<HotelDb> options) : base(options) { }
    public DbSet<Hotel> Hotels => Set<Hotel>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Review> Reviews => Set<Review>();
}

static class Db
{
    // Ждём, пока PostgreSQL поднимется, создаём таблицы и заполняем стартовыми данными.
    public static async Task InitAsync(IServiceProvider services, ILogger log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HotelDb>();
                await db.Database.EnsureCreatedAsync();
                if (!await db.Hotels.AnyAsync()) await Seed.RunAsync(db);
                log.LogInformation("hotels_db готова");
                return;
            }
            catch (Exception e) when (attempt < 30)
            {
                log.LogWarning("БД недоступна ({Msg}), попытка {N}", e.Message, attempt);
                await Task.Delay(2000);
            }
        }
    }
}

static class Seed
{
    record H(string Name, string City, string Address, int Stars, decimal Price, string Tagline, string Description, string[] Amenities);

    static readonly H[] Hotels =
    {
        new("Северный маяк", "Санкт-Петербург", "Набережная, 14", 4, 6900m,
            "Лофт в доходном доме с видом на воду.",
            "Бывший доходный дом начала века: высокие потолки, дубовый паркет и окна на реку. Завтраки подают в библиотеке на первом этаже, а с крыши видно разводные мосты.",
            new[] { "Wi-Fi", "Завтрак", "Крыша", "Библиотека" }),
        new("Графит", "Москва", "Пресненский район", 5, 14500m,
            "Тёмный дизайн-отель в центре.",
            "Сорок номеров в графитовых тонах, тяжёлые шторы и хорошая звукоизоляция. Внизу — бар без вывески, наверху — спа с хаммамом.",
            new[] { "Wi-Fi", "Спа", "Бар", "Парковка", "Спортзал" }),
        new("Сосны и туман", "Сортавала", "Ладожские шхеры", 3, 5200m,
            "Деревянные дома у озера.",
            "Двенадцать домиков в сосновом лесу на берегу. Своя баня, лодки напрокат и тишина, в которой слышно, как падают шишки.",
            new[] { "Баня", "Лодки", "Камин", "Парковка" }),
        new("Обсерватория", "Казань", "Улица Баумана", 4, 7800m,
            "Панорамная крыша над старым городом.",
            "Отель в бывшем здании типографии. На последнем этаже — терраса с телескопом и ночными экскурсиями по звёздному небу.",
            new[] { "Wi-Fi", "Терраса", "Завтрак", "Трансфер" }),
        new("Тёплый камень", "Сочи", "Курортный проспект", 4, 9900m,
            "Первая линия и сад с кипарисами.",
            "Невысокое здание из светлого камня в собственном саду. Пять минут до гальки, открытый бассейн с подогревом и ужины на веранде.",
            new[] { "Бассейн", "Пляж", "Сад", "Завтрак", "Wi-Fi" }),
        new("Байкальская тишина", "Листвянка", "Посёлок у истока Ангары", 3, 6100m,
            "Окна на Байкал и Саяны.",
            "Небольшой гостевой дом на склоне. Зимой — прогулки по прозрачному льду, летом — катер до мыса и копчёный омуль на ужин.",
            new[] { "Баня", "Завтрак", "Экскурсии", "Парковка" }),
        new("Тихая гавань", "Калининград", "Остров Канта", 4, 6400m,
            "Кирпичный пакгауз у реки.",
            "Номера в бывшем портовом складе: открытый кирпич, балки и большие окна. Велосипеды бесплатно — до косы около часа.",
            new[] { "Wi-Fi", "Велосипеды", "Кафе", "Завтрак" }),
        new("Медь", "Екатеринбург", "Бывший заводской квартал", 4, 7200m,
            "Индустриальный отель в старом цехе.",
            "Медные светильники, бетон и стекло. В лобби — галерея местных художников, на крыше — кинотеатр под открытым небом.",
            new[] { "Wi-Fi", "Галерея", "Кино", "Спортзал" }),
        new("Высота 2100", "Красная Поляна", "Горная деревня", 5, 16800m,
            "Шале у верхней станции подъёмника.",
            "Самый высокий отель курорта: лыжи до двери, панорамный бассейн с видом на хребет и камин в каждом номере.",
            new[] { "Ski-in", "Бассейн", "Спа", "Камин", "Ресторан" }),
        new("Старый дворик", "Суздаль", "Улица у Кремля", 3, 4800m,
            "Купеческая усадьба с самоваром.",
            "Деревянный дом с резными наличниками и внутренним двором. По утрам — блины и чай из самовара, вечером — тишина и колокольный звон.",
            new[] { "Завтрак", "Сад", "Парковка", "Wi-Fi" }),
        new("Ночной экспресс", "Нижний Новгород", "У Чкаловской лестницы", 4, 6700m,
            "Отель в стилистике спальных вагонов.",
            "Номера-купе с латунью и бархатом, вид на слияние Оки и Волги. Ресторан работает до утра, как вагон-ресторан.",
            new[] { "Wi-Fi", "Ресторан", "Бар", "Завтрак" }),
        new("Полярная ночь", "Териберка", "Берег Баренцева моря", 3, 8900m,
            "Стеклянные домики под северным сиянием.",
            "Пять домиков со стеклянной крышей на краю земли. Гиды будят, когда начинается сияние, а на завтрак — краб и морошка.",
            new[] { "Сияние", "Экскурсии", "Завтрак", "Трансфер" }),
    };

    static readonly string[] Authors =
    {
        "Анна К.", "Михаил Р.", "Ольга С.", "Дмитрий В.", "Екатерина Л.", "Игорь Н.", "Мария Т.", "Сергей П.",
        "Алина Б.", "Павел Ж.", "Ксения Д.", "Артём Ф.", "Наталья Ш.", "Роман Е.", "Вера М.", "Тимур Г."
    };

    static readonly (double Score, string Text)[] ReviewPool =
    {
        (9.6, "Лучший сон за весь год. Тихо, темно, кровать идеальная."),
        (9.2, "Персонал запомнил, что я пью кофе без сахара, со второго дня. Мелочь, а приятно."),
        (8.8, "Номер меньше, чем на фото, но продуман до сантиметра. Вернёмся."),
        (9.4, "Вид из окна стоит каждого рубля. Завтраки — отдельная любовь."),
        (7.9, "Всё хорошо, но парковка неудобная и Wi-Fi местами проседает."),
        (9.0, "Чисто, спокойно, без лишнего пафоса. То, что нужно после дороги."),
        (8.5, "Заселили раньше времени без доплаты. Спасибо!"),
        (9.8, "Ездили на годовщину — организовали всё лучше, чем мы мечтали."),
        (8.1, "Хорошее место, но шумоизоляция между номерами могла быть лучше."),
        (9.3, "Отличная локация: всё рядом пешком, а в номере при этом тишина."),
        (8.7, "Уютно и очень по-домашнему. Поставили бы ещё одну подушку — и было бы 10."),
        (9.5, "Атмосфера как в кино. Фотографии не передают."),
    };

    public static async Task RunAsync(HotelDb db)
    {
        var start = new DateOnly(2026, 10, 30);
        for (var i = 0; i < Hotels.Length; i++)
        {
            var s = Hotels[i];
            var hotel = new Hotel
            {
                Name = s.Name, City = s.City, Address = s.Address, Stars = s.Stars, PricePerNight = s.Price,
                Tagline = s.Tagline, Description = s.Description, Amenities = s.Amenities.ToList(),
                ImageUrl = $"/img/hotel-{i + 1}.jpg"
            };
            db.Hotels.Add(hotel);
            await db.SaveChangesAsync();

            db.Rooms.AddRange(
                new Room { HotelId = hotel.Id, Type = "Стандарт", Capacity = 2, Price = s.Price },
                new Room { HotelId = hotel.Id, Type = "Делюкс", Capacity = 3, Price = Math.Round(s.Price * 1.45m / 100) * 100 },
                new Room { HotelId = hotel.Id, Type = "Люкс", Capacity = 4, Price = Math.Round(s.Price * 2.2m / 100) * 100 });

            // 4–6 вымышленных отзывов на отель, детерминированно.
            var count = 4 + i % 3;
            var reviews = new List<Review>();
            for (var j = 0; j < count; j++)
            {
                var (score, text) = ReviewPool[(i * 5 + j * 7) % ReviewPool.Length];
                reviews.Add(new Review
                {
                    HotelId = hotel.Id,
                    Author = Authors[(i * 3 + j * 5) % Authors.Length],
                    Score = score,
                    Text = text,
                    Date = start.AddDays(-(i * 11 + j * 17) % 200)
                });
            }
            db.Reviews.AddRange(reviews);
            hotel.Rating = Math.Round(reviews.Average(r => r.Score), 1);
            hotel.ReviewsCount = reviews.Count;
            await db.SaveChangesAsync();
        }
    }
}
