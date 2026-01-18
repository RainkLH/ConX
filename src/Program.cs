using ConX.Infrastructure.Logging;
using ConX.Models;
using ConX.Repositories;
using ConX.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Configure logging (console + file under logs/, daily rolling)
var logPath = Path.Combine(AppContext.BaseDirectory, "logs");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new SimpleFileLoggerProvider(logPath, LogLevel.Information));

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();

// 读取并配置 Kestrel 监听地址（发布运行不依赖 VS 的 launchSettings）
var urls = builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://*:6857";
builder.WebHost.UseUrls(urls);

// HttpClient 基地址不要从 launchSettings 读取（该文件仅 VS 调试用）
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("http://localhost:6857") }); // 如需 https，确保证书配置好

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None; // 测试环境先关闭Secure
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.Cookie.Name = "ConXAuth";
        options.LoginPath = "/Login";
    });

builder.Services.AddAuthorization();

// Application services
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<TerminalManager>();
builder.Services.AddSingleton<ProcessService>();
builder.Services.AddHostedService<TerminalTimeoutService>();
builder.Services.AddSingleton<ConX.Infrastructure.TerminalCircuitHandler>();
builder.Services.AddSingleton<CircuitHandler>(sp => sp.GetRequiredService<ConX.Infrastructure.TerminalCircuitHandler>());

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Service is begin to run...");

// Initialize background process refresh
var processService = app.Services.GetRequiredService<ProcessService>();
await processService.Init();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Persist DataProtection keys to disk so authentication cookies survive restarts
var keysPath = Path.Combine(app.Environment.ContentRootPath, "DataProtection-Keys");
Directory.CreateDirectory(keysPath);
app.Services.GetRequiredService<IDataProtectionProvider>()
    .CreateProtector("ConX")
   ;

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapBlazorHub();
app.MapHub<ConX.Hubs.TerminalHub>("/terminalhub");
app.MapFallbackToPage("/_Host");

// Minimal auth endpoints
app.MapPost("/api/auth/login", async (LoginDto dto, ConX.Repositories.UserRepository repo, HttpContext ctx) =>
{
    var user = repo.GetUserByName(dto.UserName);
    if (user == null) return Results.Unauthorized();
    var hasher = new PasswordHasher<ConX.Models.User>();
    var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password);
    if (verify == PasswordVerificationResult.Success)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.UserName)
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new System.Security.Claims.ClaimsPrincipal(identity));
        return Results.Ok();
    }
    return Results.Unauthorized();
});

app.MapPost("/api/auth/register", (RegisterDto dto, ConX.Repositories.UserRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(dto.UserName) || string.IsNullOrWhiteSpace(dto.Password))
    {
        return Results.BadRequest(new { message = "用户名或密码不能为空" });
    }
    if (repo.UserExists(dto.UserName))
    {
        return Results.Conflict(new { message = "该用户名已注册过" });
    }
    var ok = repo.CreateUser(dto.UserName, dto.Password);
    if (!ok)
    {
        return Results.Conflict(new { message = "该用户名已注册过" });
    }
    return Results.Ok(new { message = "注册成功" });
});

app.MapPost("/api/auth/logout", async (HttpContext ctx, ILogger<Program> logger) =>
{
    // 记录注销请求
    var userName = ctx.User?.Identity?.Name ?? "Unknown";
    logger.LogInformation("Logout requested for user: {UserName}", userName);
    
    // 清除认证 Cookie
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    // // 多种方式确保 Cookie 完全被删除
    // // 方式1：使用标准的 Delete 方法
    // ctx.Response.Cookies.Delete("ConXAuth");

    // // 方式2：直接在响应头中设置过期的 Set-Cookie（最直接的方式）
    // ctx.Response.Headers["Set-Cookie"] = "ConXAuth=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; HttpOnly; SameSite=Lax";

    // // 清除可能被 SignOutAsync 设置的其他 Cookie
    // ctx.Response.Cookies.Delete(".AspNetCore.Correlation.Google");
    // ctx.Response.Cookies.Delete("ai_session");
    // ctx.Response.Cookies.Delete("ai_user");

    // 禁用浏览器缓存，防止旧状态被缓存
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    
    logger.LogInformation("User {UserName} logged out successfully", userName);
    return Results.Ok(new { message = "Logged out successfully" });
});

// Process APIs
app.MapGet("/api/processes", async (string? filter, ProcessService ps, ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("GetProcesses endpoint invoked with filter={Filter}", filter);
        var list = ps.QueryProcesses(filter);
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to get processes with filter={Filter}", filter);
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

app.MapPost("/api/processes/{pid}/kill", async (int pid, KillDto dto, ProcessService ps, ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Kill endpoint invoked for pid={Pid} reason={Reason}", pid, dto?.Reason);
        var (ok, message) = await ps.TryKillProcessAsync(pid, dto?.Reason ?? "api");
        if (ok) return Results.Ok(new { message });
        return Results.BadRequest(message);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in kill endpoint for pid={Pid}", pid);
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// quick audit query (dev/debug)
app.MapGet("/api/audit", (AuditService audit) => Results.Ok(audit.Query(0, 200)));

app.Run();

