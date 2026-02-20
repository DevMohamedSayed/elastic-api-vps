using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using Prometheus;
using Amazon.S3;
using Amazon.S3.Model;
using RabbitMQ.Client;
using Serilog;
using Serilog.Sinks.Elasticsearch;

// ── Helper: Read Docker Secret ──────────────────────────────────
string ReadSecret(string name, string fallback)
{
    var path = "/run/secrets/" + name;
    try { return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback; }
    catch { return fallback; }
}

// ── Serilog Setup (replaces default logger) ────────────────────
var esUrl = Environment.GetEnvironmentVariable("ELASTICSEARCH_URL") ?? "http://localhost:9200";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(esUrl))
    {
        AutoRegisterTemplate = true,
        IndexFormat = "api-logs-{0:yyyy.MM.dd}",
        ModifyConnectionSettings = conn =>
            conn.BasicAuthentication("elastic", "elastic123")
    })
    .Enrich.WithProperty("Application", "ElasticApi")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var jwtKey = ReadSecret("jwt_secret", Environment.GetEnvironmentVariable("JWT_SECRET") ?? "MyS3cur3K3y!2026VPS-Pr0ject!!XYZ");
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Mohamed Sayed - VPS API", Version = "v7.0" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Description = "Enter your JWT token",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "mohamedsayed.site",
            ValidAudience = "mohamedsayed.site",
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
        };
    });
builder.Services.AddAuthorization();

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

// Elasticsearch client
var esSettings = new ElasticsearchClientSettings(new Uri(esUrl))
    .Authentication(new BasicAuthentication("elastic", "elastic123"))
    .DisableDirectStreaming();
builder.Services.AddSingleton(new ElasticsearchClient(esSettings));

// SQL Server
var sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION")
    ?? "Server=localhost;Database=ProjectsDb;User Id=sa;Password=SqlServer2026!;TrustServerCertificate=true";
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(sqlConn));

// MinIO
var minioEndpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT") ?? "localhost:9002";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "minioadmin";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "Minio2026Secret!";
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    minioAccessKey, minioSecretKey,
    new AmazonS3Config { ServiceURL = $"http://{minioEndpoint}", ForcePathStyle = true }));

// ── NEW: Redis Cache ───────────────────────────────────────────
var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConn;
    options.InstanceName = "api_";
});

// ── NEW: RabbitMQ Connection ───────────────────────────────────
var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
var rabbitUser = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "admin";
var rabbitPass = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? "RabbitMQ2026!";

builder.Services.AddSingleton<IConnection>(_ =>
{
    var factory = new ConnectionFactory
    {
        HostName = rabbitHost,
        UserName = rabbitUser,
        Password = rabbitPass
    };
    return factory.CreateConnection();
});

builder.Services.AddHostedService<ProjectEventConsumer>();
builder.Services.AddSingleton<SseConnectionManager>();
// Health Checks
builder.Services.AddHealthChecks()
    .AddSqlServer(sqlConn, name: "sqlserver")
    .AddElasticsearch($"http://elastic:elastic123@elasticsearch:9200", name: "elasticsearch")
    .AddRedis(redisConn, name: "redis");

var app = builder.Build();

// Middleware
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("v1/swagger.json", "VPS API v7.0"));
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseHttpMetrics();

// Auto-create database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Ensure MinIO bucket
using (var scope = app.Services.CreateScope())
{
    var s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
    try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = "uploads" }); }
    catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists") { }
}

// ── Auth ──────────────────────────────────────────────────────
app.MapPost("/auth/login", async (LoginRequest request) =>
{
    // Validate Turnstile
    if (!string.IsNullOrEmpty(request.TurnstileToken))
    {
        using var http = new HttpClient();
        var turnstileResp = await http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "secret", Environment.GetEnvironmentVariable("TURNSTILE_SECRET") ?? "" },
                { "response", request.TurnstileToken }
            }));
        var result = await turnstileResp.Content.ReadAsStringAsync();
        Log.Information("Turnstile: {Result}", result);
        if (!result.Contains("true"))
        {
            Log.Warning("Turnstile failed");
            return Results.BadRequest(new { error = "Bot check failed" });
        }
    }

    if (request.Username?.Trim() == "admin" && request.Password?.Trim() == "Admin2026!")
    {
        var claims = new[] { new Claim(ClaimTypes.Name, request.Username), new Claim(ClaimTypes.Role, "Admin") };
        var token = new JwtSecurityToken(
            issuer: "mohamedsayed.site", audience: "mohamedsayed.site", claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256));
        Log.Information("User {U} logged in", request.Username);
        return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
    Log.Warning("Failed login for {U}", request.Username);
    return Results.Unauthorized();
}).WithTags("Auth");

app.MapHealthChecks("/health");

// ── Public Endpoints ──────────────────────────────────────────
app.MapGet("/", () => "Elastic API v7.0 - Redis + RabbitMQ + Serilog")
    .WithTags("General");
app.MapGet("/robots.txt", () =>
{
    var content = "User-agent: *\nAllow: /\nDisallow: /api/swagger\nDisallow: /grafana/\nDisallow: /kibana/\n\nSitemap: https://mohamedsayed.site/api/sitemap.xml";
    return Results.Text(content, "text/plain");
}).WithTags("SEO");

app.MapGet("/sitemap.xml", async (AppDbContext db) =>
{
    var projects = await db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync();
    var urls = projects.Select(p =>
        $"  <url>\n    <loc>https://mohamedsayed.site/projects/{p.Slug}</loc>\n    <lastmod>{p.UpdatedAt?.ToString("yyyy-MM-dd") ?? p.CreatedAt.ToString("yyyy-MM-dd")}</lastmod>\n    <changefreq>weekly</changefreq>\n  </url>");

    var sitemap = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n  <url>\n    <loc>https://mohamedsayed.site</loc>\n    <changefreq>daily</changefreq>\n    <priority>1.0</priority>\n  </url>\n{string.Join("\n", urls)}\n</urlset>";

    return Results.Text(sitemap, "application/xml");
}).WithTags("SEO");


app.MapGet("/users/search/{city}", async (string city, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("users").Query(q => q.Match(m => m.Field("city").Query(city))).Size(10));
    return response.IsValidResponse ? Results.Ok(response.Documents) : Results.Problem("ES query failed");
}).WithTags("Search");

app.MapGet("/products/search/{category}", async (string category, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("products").Query(q => q.Match(m => m.Field("category").Query(category))).Size(10));
    return response.IsValidResponse ? Results.Ok(response.Documents) : Results.Problem("ES query failed");
}).WithTags("Search");

app.MapGet("/logs/errors", async (ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("logs").Query(q => q.Match(m => m.Field("level").Query("ERROR"))).Size(10));
    return response.IsValidResponse ? Results.Ok(response.Documents) : Results.Problem("ES query failed");
}).WithTags("Search");

// ── Projects with Redis Cache ─────────────────────────────────
app.MapGet("/projects", async (AppDbContext db, IDistributedCache cache) =>
{
    // Try cache first
    var cached = await cache.GetStringAsync("projects:all");
    if (cached is not null)
    {
        Log.Information("Cache HIT for projects:all");
        return Results.Ok(JsonSerializer.Deserialize<List<Project>>(cached));
    }

    // Cache miss — query SQL
    Log.Information("Cache MISS for projects:all — querying SQL Server");
    var projects = await db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync();
    await cache.SetStringAsync("projects:all",
        JsonSerializer.Serialize(projects),
        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) });
    return Results.Ok(projects);
}).RequireRateLimiting("fixed").WithTags("Projects");

app.MapGet("/projects/{id}", async (int id, AppDbContext db) =>
    await db.Projects.FindAsync(id) is Project p
        ? Results.Ok(p)
        : Results.NotFound(new { error = "Project not found" }))
    .RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

app.MapPost("/projects", async (Project project, AppDbContext db, IDistributedCache cache, IConnection rabbit) =>
{
    project.CreatedAt = DateTime.UtcNow;
    project.Slug = SlugHelper.GenerateSlug(project.Name);
    db.Projects.Add(project);
    await db.SaveChangesAsync();

    // Invalidate cache (data changed, cache is stale)
    await cache.RemoveAsync("projects:all");

    // Publish event to RabbitMQ for async processing
    using var channel = rabbit.CreateModel();
    channel.QueueDeclare("project-events", durable: true, exclusive: false, autoDelete: false);
    var message = JsonSerializer.Serialize(new { Event = "ProjectCreated", project.Id, project.Name, Timestamp = DateTime.UtcNow });
    channel.BasicPublish("", "project-events", null, Encoding.UTF8.GetBytes(message));
    Log.Information("Published ProjectCreated event for {ProjectName}", project.Name);

    return Results.Created($"/projects/{project.Id}", project);
}).RequireRateLimiting("fixed").WithTags("Projects");

app.MapGet("/projects/by-slug/{slug}", async (string slug, AppDbContext db) =>
    await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug) is Project p
        ? Results.Ok(p)
        : Results.NotFound(new { error = "Project not found" }))
    .WithTags("Projects");

app.MapGet("/projects/{slug}/meta", async (string slug, AppDbContext db) =>
{
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
    if (project is null) return Results.NotFound();
    var jsonLd = new
    {
        context = "https://schema.org",
        type = "SoftwareApplication",
        name = project.Name,
        description = project.Description,
        url = $"https://mohamedsayed.site/projects/{project.Slug}",
        dateCreated = project.CreatedAt.ToString("yyyy-MM-dd"),
        applicationCategory = "DeveloperApplication"
    };
    return Results.Ok(jsonLd);
}).WithTags("SEO");

app.MapPut("/projects/{id}", async (int id, Project input, AppDbContext db, IDistributedCache cache) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound(new { error = "Project not found" });
    project.Name = input.Name;
    project.Description = input.Description;
    project.Status = input.Status;
    project.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await cache.RemoveAsync("projects:all");
    return Results.Ok(project);
}).RequireRateLimiting("fixed").WithTags("Projects");

app.MapDelete("/projects/{id}", async (int id, AppDbContext db, IDistributedCache cache) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound(new { error = "Project not found" });
    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    await cache.RemoveAsync("projects:all");
    return Results.Ok(new { message = "Deleted", id });
}).RequireRateLimiting("fixed").WithTags("Projects");

// ── File Endpoints ────────────────────────────────────────────
app.MapPost("/files/upload", async (HttpRequest request, IAmazonS3 s3) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest(new { error = "No file provided" });
    using var stream = file.OpenReadStream();
    var key = $"{Guid.NewGuid()}-{file.FileName}";
    await s3.PutObjectAsync(new PutObjectRequest
    {
        BucketName = "uploads", Key = key,
        InputStream = stream, ContentType = file.ContentType
    });
    Log.Information("File uploaded: {FileName} ({Size} bytes)", file.FileName, file.Length);
    return Results.Ok(new { message = "Uploaded", key, size = file.Length });
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Files");

app.MapGet("/files", async (IAmazonS3 s3) =>
{
    var response = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "uploads" });
    return Results.Ok(response.S3Objects.Select(o => new { o.Key, o.Size, o.LastModified }));
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Files");

app.MapGet("/files/{key}", async (string key, IAmazonS3 s3) =>
{
    try
    {
        var response = await s3.GetObjectAsync("uploads", key);
        return Results.File(response.ResponseStream, response.Headers.ContentType, key);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "File not found" });
    }
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Files");

app.MapDelete("/files/{key}", async (string key, IAmazonS3 s3) =>
{
    await s3.DeleteObjectAsync("uploads", key);
    return Results.Ok(new { message = "Deleted", key });
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Files");

// ── Server-Side Rendered page with SEO + React SPA ──────────
app.MapGet("/projects/{slug}/page", async (string slug, AppDbContext db) =>
{
    var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
    if (project is null) return Results.NotFound("Project not found");

    var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>{project.Name} | Mohamed Sayed</title>
    <meta name=""description"" content=""{project.Description}"" />
    <meta property=""og:title"" content=""{project.Name}"" />
    <meta property=""og:description"" content=""{project.Description}"" />
    <meta property=""og:url"" content=""https://mohamedsayed.site/projects/{project.Slug}"" />
    <meta property=""og:type"" content=""article"" />
    <script type=""application/ld+json"">
    {{
        ""@context"": ""https://schema.org"",
        ""@type"": ""Article"",
        ""headline"": ""{project.Name}"",
        ""description"": ""{project.Description}"",
        ""url"": ""https://mohamedsayed.site/projects/{project.Slug}"",
        ""datePublished"": ""{project.CreatedAt:yyyy-MM-dd}"",
        ""author"": {{
            ""@type"": ""Person"",
            ""name"": ""Mohamed Sayed""
        }}
    }}
    </script>
</head>
<body>
    <div id=""root""></div>
    <noscript>
        <h1>{project.Name}</h1>
        <p>{project.Description}</p>
        <p>Status: {project.Status}</p>
        <a href=""https://mohamedsayed.site"">Back to Home</a>
    </noscript>
    <script>
        // Load React app dynamically
        fetch(/).then(r => r.text()).then(html => {{
            var match = html.match(/src=""([^""]*\.js)""/);
            var cssMatch = html.match(/href=""([^""]*\.css)""/);
            if (cssMatch) {{
                var link = document.createElement(link);
                link.rel = stylesheet;
                link.href = cssMatch[1];
                document.head.appendChild(link);
            }}
            if (match) {{
                var script = document.createElement(script);
                script.type = module;
                script.src = match[1];
                document.body.appendChild(script);
            }}
        }});
    </script>
</body>
</html>";
    return Results.Content(html, "text/html");
}).WithTags("SEO");

app.MapGet("/events", async (HttpContext ctx, SseConnectionManager sse) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var id = Guid.NewGuid().ToString();
    sse.AddConnection(id, ctx.Response);
    Log.Information("SSE client connected: {Id}", id);

    await ctx.Response.WriteAsync("event: connected" + "
" + "data: {"status":"ok"}" + "

");
    await ctx.Response.Body.FlushAsync();

    var tcs = new TaskCompletionSource();
    ctx.RequestAborted.Register(() => tcs.TrySetResult());
    await tcs.Task;

    sse.RemoveConnection(id);
}).WithTags("Events");

app.MapMetrics();
app.Run("http://0.0.0.0:5000");

public static class SlugHelper
{
    public static string GenerateSlug(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text.ToLower(), @"[^a-z0-9]+", "-").Trim('-');
    }
}

// ── Models ────────────────────────────────────────────────────
public class Project
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string TurnstileToken { get; set; } = "";
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Project> Projects => Set<Project>();
}

public class ProjectEventConsumer : BackgroundService
{
    private readonly IConnection _connection;
    private readonly ILogger<ProjectEventConsumer> _logger;

    public ProjectEventConsumer(IConnection connection, ILogger<ProjectEventConsumer> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _connection.CreateModel();
        channel.QueueDeclare("project-events", durable: true, exclusive: false, autoDelete: false);

        var consumer = new RabbitMQ.Client.Events.EventingBasicConsumer(channel);
        consumer.Received += (sender, args) =>
        {
            var body = Encoding.UTF8.GetString(args.Body.ToArray());
            _logger.LogInformation("Consumed message: {Message}", body);

            // This is where you would:
            // 1. Index the project in Elasticsearch for search
            // 2. Send a notification email
            // 3. Generate a PDF report
            // 4. Any other slow/heavy work

            channel.BasicAck(args.DeliveryTag, false);
            _logger.LogInformation("Message processed and acknowledged");
        };

        channel.BasicConsume("project-events", autoAck: false, consumer: consumer);
        _logger.LogInformation("ProjectEventConsumer started — listening for messages");

        return Task.CompletedTask;
    }
}

public class SseConnectionManager
{
    private readonly List<(string Id, HttpResponse Response)> _connections = new();
    private readonly object _lock = new();

    public void AddConnection(string id, HttpResponse response)
    {
        lock (_lock) _connections.Add((id, response));
    }

    public void RemoveConnection(string id)
    {
        lock (_lock) _connections.RemoveAll(c => c.Id == id);
    }

    public async Task BroadcastAsync(string eventName, string data)
    {
        List<(string Id, HttpResponse Response)> snapshot;
        lock (_lock) snapshot = new(_connections);

        foreach (var (id, response) in snapshot)
        {
            try
            {
                await response.WriteAsync("event: " + eventName + "\n");
                await response.WriteAsync("data: " + data + "\n\n");
                await response.Body.FlushAsync();
            }
            catch
            {
                RemoveConnection(id);
            }
        }
    }
}
