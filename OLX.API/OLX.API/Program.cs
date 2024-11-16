using Olx.DAL.Exstension;
using OLX.API.Extensions;
using OLX.API.Middlewares;
using System.Globalization;

var defaultCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();

builder.Services.AddOlxApiServicesAndConfigurations(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseCors("AllowOrigins");
app.DataBaseMigrate();
await app.SeedDataAsync();
app.AddStaticFiles();

app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
