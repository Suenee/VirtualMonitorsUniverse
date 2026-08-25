var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new
{
    service = "Virtual Monitors Universe",
    status = "ok"
}));

app.Run();
