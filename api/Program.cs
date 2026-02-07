using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

var builder = WebApplication.CreateBuilder(args);

var esUrl = Environment.GetEnvironmentVariable("ELASTICSEARCH_URL") ?? "http://localhost:9200";

var settings = new ElasticsearchClientSettings(new Uri(esUrl))
    .Authentication(new BasicAuthentication("elastic", "elastic123"))
    .DisableDirectStreaming();

builder.Services.AddSingleton(new ElasticsearchClient(settings));

var app = builder.Build();

app.MapGet("/", () => "Elastic API v3.0 - Deployed by deployer!");

app.MapGet("/users/search/{city}", async (string city, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("users")
        .Query(q => q.Match(m => m.Field("city").Query(city)))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
});

app.MapGet("/products/search/{category}", async (string category, ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("products")
        .Query(q => q.Match(m => m.Field("category").Query(category)))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
});

app.MapGet("/logs/errors", async (ElasticsearchClient client) =>
{
    var response = await client.SearchAsync<JsonElement>(s => s
        .Indices("logs")
        .Query(q => q.Match(m => m.Field("level").Query("ERROR")))
        .Size(10));
    return response.IsValidResponse
        ? Results.Ok(response.Documents)
        : Results.Problem("ES query failed: " + response.DebugInformation);
});

app.Run("http://0.0.0.0:5000");
