var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => new { status = "Healthy", service = "user-service" });

app.MapGet("/users/{id}", (int id) =>
{
    return Results.Ok(new 
    { 
        Id = id, 
        Name = id == 1 ? "Евгения Диколенко" : "Дмитрий Морозов", 
        Role = "Student",
        Email = "user@it-hub.ru" 
    });
});

app.Run();
