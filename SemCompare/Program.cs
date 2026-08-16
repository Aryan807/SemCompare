using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SemCompare.Data;
using SemCompare.Services;

var builder = WebApplication.CreateBuilder(args);

var geminiRpm = builder.Configuration.GetValue<int?>("Gemini:MaxRequestsPerMinute") ?? 5;
AiService.ConfigureRateLimit(geminiRpm);

// ── Razor / Blazor ────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<DiffDbContext>(options =>
    options.UseSqlite("Data Source=diff.db"));

// ── GitHub OAuth ──────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = "GitHub";
})
.AddCookie(options =>
{
    options.LoginPath        = "/login";
    options.LogoutPath       = "/logout";
    options.Cookie.Name      = "SemCompare.Auth";
    options.Cookie.HttpOnly  = true;
    options.Cookie.SameSite  = SameSiteMode.Lax;
    options.ExpireTimeSpan   = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
})
.AddGitHub("GitHub", options =>
{
    options.ClientId     = builder.Configuration["GitHub:ClientId"]     ?? "";
    options.ClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? "";
    options.Scope.Add("repo");          // read public + private repos
    options.Scope.Add("read:user");
    // Persist the access token in the auth cookie so Blazor components can use it
    options.SaveTokens = true;

    var callbackPath = builder.Configuration["GitHub:CallbackPath"];
    options.CallbackPath = string.IsNullOrWhiteSpace(callbackPath)
        ? "/signin-github"
        : callbackPath;

    options.Events.OnRedirectToAuthorizationEndpoint = context =>
    {
        var redirectUri = context.RedirectUri;

        if (context.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrWhiteSpace(prompt))
            redirectUri = QueryHelpers.AddQueryString(redirectUri, "prompt", prompt);

        if (context.Properties.Items.TryGetValue("login", out var login) && !string.IsNullOrWhiteSpace(login))
            redirectUri = QueryHelpers.AddQueryString(redirectUri, "login", login);

        context.Response.Redirect(redirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient<AiService>(client =>
{
    var apiKey = builder.Configuration["Gemini:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
    }
});

builder.Services.AddScoped<GitService>();
builder.Services.AddScoped<GitHubService>();
builder.Services.AddScoped<DiffService>();

// IHttpContextAccessor needed so Blazor components can read the access token
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthStateService>();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DiffDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Auth endpoints (MVC-style, not Blazor) ────────────────────────────────────

// Trigger GitHub OAuth flow
app.MapGet("/login", async (HttpContext ctx, string? returnUrl, bool switchAccount, string? loginHint) =>
{
    if (switchAccount)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    var props = new AuthenticationProperties
    {
        RedirectUri = returnUrl ?? "/"
    };

    if (switchAccount)
    {
        props.Items["prompt"] = "select_account";
    }

    if (!string.IsNullOrWhiteSpace(loginHint))
    {
        props.Items["login"] = loginHint;
    }

    return Results.Challenge(props, ["GitHub"]);
});

// Sign out
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapRazorComponents<SemCompare.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();
