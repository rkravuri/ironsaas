// Iron.Host.Fleet: see README.md in this folder.
// T1 only starts the host and answers the liveness probe. Module composition arrives in T7.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();
