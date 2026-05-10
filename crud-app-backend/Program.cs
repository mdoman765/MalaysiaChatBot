using crud_app_backend;
using crud_app_backend.Bot.Services;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

builder.Services.AddDbContextPool<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")),
    poolSize: 128);

// ── Repositories (only what UAE bot needs) ────────────────────────────────────
builder.Services.AddScoped<IWhatsAppSessionRepository, WhatsAppSessionRepository>();
builder.Services.AddScoped<IWhatsAppMessageRepository, WhatsAppMessageRepository>();

// ── Session service ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IWhatsAppSessionService, WhatsAppSessionService>();

// ── UAE Bot services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<BotStateService>();   // per-user locks + burst detection
builder.Services.AddSingleton<WebhookQueue>();       // Channel for instant 200 OK
builder.Services.AddScoped<IUaeBotService, UaeBotService>();
builder.Services.AddScoped<IDialogClient, DialogClient>();
builder.Services.AddScoped<IUaeCrmService, UaeCrmService>();

// ── Background services ───────────────────────────────────────────────────────
builder.Services.AddHostedService<WebhookProcessorService>();
builder.Services.AddHostedService<KeepAliveService>();

// ── HTTP clients ──────────────────────────────────────────────────────────────

// 360dialog
builder.Services.AddHttpClient("Dialog", client =>
{
    var key = builder.Configuration["Dialog:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Add("D360-API-KEY", key);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// CRM (complaint / return / agent)
builder.Services.AddHttpClient("CrmClient", client =>
{
    var key = builder.Configuration["Crm:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Add("access-token", key);
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ── Form limits ───────────────────────────────────────────────────────────────
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 25 * 1024 * 1024;
});

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── EF Core warm-up ───────────────────────────────────────────────────────────
using (var warmupScope = app.Services.CreateScope())
{
    var db = warmupScope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await db.WhatsAppSessions
            .AsNoTracking()
            .Select(s => s.Phone)
            .FirstOrDefaultAsync();
    }
    catch { /* DB not yet reachable — ignore */ }
}

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

app.Run();
