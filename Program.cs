using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var connectionString = config.GetConnectionString("Postgres") ?? config["ConnectionStrings__Postgres"] ?? throw new InvalidOperationException("A Postgres connection string is required.");
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 15 * 1024 * 1024);

var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/";
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
});
var providers = new List<string>();
if (Configured("Google")) { auth.AddGoogle("Google", o => { o.ClientId = config["Authentication:Google:ClientId"]!; o.ClientSecret = config["Authentication:Google:ClientSecret"]!; o.Events.OnCreatingTicket = c => { c.Identity?.AddClaim(new Claim("snip_provider", "Google")); return Task.CompletedTask; }; }); providers.Add("Google"); }
if (Configured("GitHub")) { auth.AddOAuth("GitHub", o =>
{
    o.ClientId = config["Authentication:GitHub:ClientId"]!; o.ClientSecret = config["Authentication:GitHub:ClientSecret"]!; o.CallbackPath = "/signin-github";
    o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize"; o.TokenEndpoint = "https://github.com/login/oauth/access_token"; o.UserInformationEndpoint = "https://api.github.com/user"; o.Scope.Add("read:user");
    o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id"); o.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
    o.Events = new OAuthEvents { OnCreatingTicket = async c => { using var request = new HttpRequestMessage(HttpMethod.Get, c.Options.UserInformationEndpoint); request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.AccessToken); request.Headers.UserAgent.ParseAdd("SnipScratchpad"); using var response = await c.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c.HttpContext.RequestAborted); response.EnsureSuccessStatusCode(); using var user = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(c.HttpContext.RequestAborted)); c.RunClaimActions(user.RootElement); c.Identity?.AddClaim(new Claim("snip_provider", "GitHub")); } };
}); providers.Add("GitHub"); }
if (Configured("Microsoft")) { auth.AddMicrosoftAccount("Microsoft", o => { o.ClientId = config["Authentication:Microsoft:ClientId"]!; o.ClientSecret = config["Authentication:Microsoft:ClientSecret"]!; o.Events.OnCreatingTicket = c => { c.Identity?.AddClaim(new Claim("snip_provider", "Microsoft")); return Task.CompletedTask; }; }); providers.Add("Microsoft"); }
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate"
});
app.Use(async (context, next) =>
{
    await next();
    if (context.Request.Path.StartsWithSegments("/signin-") || context.Request.Path.StartsWithSegments("/api/auth"))
        app.Logger.LogInformation("{Method} {Path} returned {StatusCode}; authenticated: {Authenticated}", context.Request.Method, context.Request.Path, context.Response.StatusCode, context.User.Identity?.IsAuthenticated ?? false);
});
app.UseAuthentication(); app.UseAuthorization();
await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
    await connection.ExecuteAsync("""CREATE TABLE IF NOT EXISTS snippets (id uuid PRIMARY KEY, owner_id varchar(300) NOT NULL, title varchar(200) NOT NULL, content text NULL, image_data bytea NULL, image_content_type varchar(100) NULL, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), CONSTRAINT snippet_has_payload CHECK (content IS NOT NULL OR image_data IS NOT NULL)); CREATE INDEX IF NOT EXISTS ix_snippets_owner_created ON snippets (owner_id, created_at DESC); CREATE INDEX IF NOT EXISTS ix_snippets_owner_updated ON snippets (owner_id, updated_at DESC); CREATE INDEX IF NOT EXISTS ix_snippets_owner_title ON snippets (owner_id, lower(title));""");

app.MapGet("/api/auth/providers", () => Results.Ok(providers)).AllowAnonymous();
app.MapGet("/login/{provider}", (string provider) => providers.Contains(provider, StringComparer.OrdinalIgnoreCase) ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [providers.Single(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase))]) : Results.NotFound()).AllowAnonymous();
app.MapPost("/api/auth/logout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme]));
app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new { name = user.Identity?.Name ?? "Signed in" })).RequireAuthorization();

var snippets = app.MapGroup("/api/snippets").RequireAuthorization();
snippets.MapGet("", async (NpgsqlDataSource source, ClaimsPrincipal user, string? search, string? sort) =>
{
    var order = sort?.ToLowerInvariant() switch { "title" => "title ASC, updated_at DESC", "oldest" => "updated_at ASC", _ => "updated_at DESC" };
    await using var connection = await source.OpenConnectionAsync();
    var sql = $"""SELECT id AS "Id", title AS "Title", content AS "Content", image_data IS NOT NULL AS "IsImage", image_content_type AS "ImageContentType", created_at AS "CreatedAt", updated_at AS "UpdatedAt" FROM snippets WHERE owner_id=@ownerId AND (@search IS NULL OR @search='' OR title ILIKE '%' || @search || '%' OR content ILIKE '%' || @search || '%') ORDER BY {order}""";
    return Results.Ok(await connection.QueryAsync<SnippetListItem>(sql, new { ownerId = OwnerId(user), search }));
});
snippets.MapGet("/{id:guid}", async (NpgsqlDataSource source, ClaimsPrincipal user, Guid id) => { await using var c = await source.OpenConnectionAsync(); var item = await c.QuerySingleOrDefaultAsync<Snippet>("SELECT id AS \"Id\", title AS \"Title\", content AS \"Content\", image_content_type AS \"ImageContentType\", created_at AS \"CreatedAt\", updated_at AS \"UpdatedAt\" FROM snippets WHERE id=@id AND owner_id=@ownerId", new { id, ownerId = OwnerId(user) }); return item is null ? Results.NotFound() : Results.Ok(item); });
snippets.MapGet("/{id:guid}/image", async (NpgsqlDataSource source, ClaimsPrincipal user, Guid id) => { await using var c = await source.OpenConnectionAsync(); var image = await c.QuerySingleOrDefaultAsync<ImagePayload>("SELECT image_data AS \"Data\", image_content_type AS \"ContentType\" FROM snippets WHERE id=@id AND owner_id=@ownerId", new { id, ownerId = OwnerId(user) }); return image?.Data is null ? Results.NotFound() : Results.File(image.Data, image.ContentType ?? "application/octet-stream"); });
snippets.MapPost("", async (NpgsqlDataSource source, ClaimsPrincipal user, SnippetInput input) => { if (Validate(input) is { } error) return Results.BadRequest(new { error }); var id = Guid.NewGuid(); await using var c = await source.OpenConnectionAsync(); await c.ExecuteAsync("INSERT INTO snippets (id,owner_id,title,content,image_data,image_content_type) VALUES (@id,@ownerId,@title,@content,@imageData,@imageContentType)", new { id, ownerId = OwnerId(user), title = input.Title.Trim(), input.Content, input.ImageData, input.ImageContentType }); return Results.Created($"/api/snippets/{id}", new { id }); });
snippets.MapPut("/{id:guid}", async (NpgsqlDataSource source, ClaimsPrincipal user, Guid id, SnippetInput input) => { if (Validate(input) is { } error) return Results.BadRequest(new { error }); await using var c = await source.OpenConnectionAsync(); var rows = await c.ExecuteAsync("UPDATE snippets SET title=@title,content=@content,image_data=@imageData,image_content_type=@imageContentType,updated_at=now() WHERE id=@id AND owner_id=@ownerId", new { id, ownerId = OwnerId(user), title = input.Title.Trim(), input.Content, input.ImageData, input.ImageContentType }); return rows == 0 ? Results.NotFound() : Results.NoContent(); });
snippets.MapDelete("/{id:guid}", async (NpgsqlDataSource source, ClaimsPrincipal user, Guid id) => { await using var c = await source.OpenConnectionAsync(); return await c.ExecuteAsync("DELETE FROM snippets WHERE id=@id AND owner_id=@ownerId", new { id, ownerId = OwnerId(user) }) == 0 ? Results.NotFound() : Results.NoContent(); });
app.Run();

bool Configured(string provider) => !string.IsNullOrWhiteSpace(config[$"Authentication:{provider}:ClientId"]) && !string.IsNullOrWhiteSpace(config[$"Authentication:{provider}:ClientSecret"]);
static string OwnerId(ClaimsPrincipal user) => $"{user.FindFirstValue("snip_provider") ?? "external"}:{user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()}";
static string? Validate(SnippetInput x) => string.IsNullOrWhiteSpace(x.Title) || x.Title.Trim().Length > 200 ? "A title of 1–200 characters is required." : x.ImageData is { Length: > 10 * 1024 * 1024 } ? "Images must be 10 MB or smaller." : x.ImageData is not null && !string.IsNullOrWhiteSpace(x.Content) ? "A snip can contain either text or one image." : x.ImageData is null && x.Content is null ? "Add text or an image." : x.ImageData is not null && !(x.ImageContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) ? "Only image uploads are supported." : null;
record SnippetInput(string Title, string? Content, byte[]? ImageData, string? ImageContentType);
record Snippet(Guid Id, string Title, string? Content, string? ImageContentType, DateTime CreatedAt, DateTime UpdatedAt);
record SnippetListItem(Guid Id, string Title, string? Content, bool IsImage, string? ImageContentType, DateTime CreatedAt, DateTime UpdatedAt);
record ImagePayload(byte[]? Data, string? ContentType);
