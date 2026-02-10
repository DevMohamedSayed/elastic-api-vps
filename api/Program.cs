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
using Prometheus;
using Amazon.S3;
using Amazon.S3.Model;

// ── App Setup ──────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

var jwtKey = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "MyS3cur3K3y!2026VPS-Pr0ject!!XYZ";
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Mohamed Sayed - VPS API", Version = "v6.0" });
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

// Elasticsearch
var esUrl = Environment.GetEnvironmentVariable("ELASTICSEARCH_URL") ?? "http://localhost:9200";
var esSettings = new ElasticsearchClientSettings(new Uri(esUrl))
    .Authentication(new BasicAuthentication("elastic", "elastic123"))
    .DisableDirectStreaming();
builder.Services.AddSingleton(new ElasticsearchClient(esSettings));

// SQL Server via EF Core
var sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION")
    ?? "Server=localhost;Database=ProjectsDb;User Id=sa;Password=SqlServer2026!;TrustServerCertificate=true";
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(sqlConn));

// MinIO (S3-compatible) client
var minioEndpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT") ?? "localhost:9002";
var minioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "minioadmin";
var minioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "Minio2026Secret!";
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    minioAccessKey, minioSecretKey,
    new AmazonS3Config { ServiceURL = $"http://{minioEndpoint}", ForcePathStyle = true }));

// Redis connection string for health check
var redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";

// Health Checks
builder.Services.AddHealthChecks()
    .AddSqlServer(sqlConn, name: "sqlserver")
    .AddElasticsearch($"http://elastic:elastic123@elasticsearch:9200", name: "elasticsearch")
    .AddRedis(redisConn, name: "redis");

var app = builder.Build();

// ── Middleware Pipeline ────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("v1/swagger.json", "VPS API v6.0"));


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

// ── Auth Endpoint ─────────────────────────────────────────────
app.MapPost("/auth/login", (LoginRequest request) =>
{
    // Simple demo auth — production would check a database
    if (request.Username == "admin" && request.Password == "Admin2026!")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, request.Username),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var token = new JwtSecurityToken(
            issuer: "mohamedsayed.site",
            audience: "mohamedsayed.site",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256)
        );
        return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
    return Results.Unauthorized();
}).WithTags("Auth");

// ── Health Check ──────────────────────────────────────────────
app.MapHealthChecks("/health");

// ── Public Endpoints (no auth needed) ─────────────────────────
app.MapGet("/", () => "Elastic API v6.0 - JWT + Rate Limiting + Health Checks")
    .WithTags("General");

app.MapGet("/users/search/{city}", async (string city, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("users")
        .Query(q => q.Match(m => m.Field("city").Query(city)))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
}).WithTags("Search");

app.MapGet("/products/search/{category}", async (string category, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("products")
        .Query(q => q.Match(m => m.Field("category").Query(category)))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
}).WithTags("Search");

app.MapGet("/logs/errors", async (ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("logs")
        .Query(q => q.Match(m => m.Field("level").Query("ERROR")))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
}).WithTags("Search");

// ── Protected Endpoints (JWT required) ────────────────────────
app.MapGet("/projects", async (AppDbContext db) =>
    await db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync())
    .RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

app.MapGet("/projects/{id}", async (int id, AppDbContext db) =>
    await db.Projects.FindAsync(id) is Project p
        ? Results.Ok(p)
        : Results.NotFound(new { error = "Project not found" }))
    .RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

app.MapPost("/projects", async (Project project, AppDbContext db) =>
{
    project.CreatedAt = DateTime.UtcNow;
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    return Results.Created($"/projects/{project.Id}", project);
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

app.MapPut("/projects/{id}", async (int id, Project input, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound(new { error = "Project not found" });
    project.Name = input.Name;
    project.Description = input.Description;
    project.Status = input.Status;
    project.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(project);
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

app.MapDelete("/projects/{id}", async (int id, AppDbContext db) =>
{
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound(new { error = "Project not found" });
    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Deleted", id });
}).RequireAuthorization().RequireRateLimiting("fixed").WithTags("Projects");

// ── File Endpoints (JWT required) ─────────────────────────────
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

// Prometheus
app.MapMetrics();

app.Run("http://0.0.0.0:5000");

// ── Models ────────────────────────────────────────────────────
public class Project
{
    public int Id { get; set; }
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
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Project> Projects => Set<Project>();
}
