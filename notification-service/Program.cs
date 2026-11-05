var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var notifications = new List<string>();

app.MapGet("/health", () => new { status = "Healthy", service = "notification-service" });

app.MapGet("/notifications", () => notifications);

app.MapPost("/notifications", (NotificationDto dto) =>
{
    notifications.Add($"[{DateTime.UtcNow}] Бронь #{dto.BookingId} для {dto.CustomerName} успешно создана!");
    return Results.Ok(new { message = "Уведомление зафиксировано" });
});

app.Run();

record NotificationDto(int BookingId, string CustomerName);
