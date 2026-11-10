// Entry point of the School Admission System API.

var builder = WebApplication.CreateBuilder(args);

// The React UI runs on a different origin during development,
// so the API allows cross-origin requests.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();

// Simple liveness endpoint used by Docker health checks and by us to verify the API is up.
app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    service = "admission-api",
    time = DateTime.UtcNow
}));

app.Run();

// Required so integration tests (WebApplicationFactory) can boot this app later.
public partial class Program { }