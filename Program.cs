using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ChatRoom2.Data;
using ChatRoom2.Hubs;
using ChatRoom2.Services;
using ChatRoom2.Models;

// 1. 确保配置文件存在，否则生成默认配置
EnsureConfigFileExists(args);

var builder = WebApplication.CreateBuilder(args);

// 强制指定 wwwroot 路径（用于上传文件）
builder.Environment.WebRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");

// 转发头（获取真实IP）
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// 大文件上传限制
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2048L * 1024 * 1024; // 2GB
});

// 2. 数据库
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseMySql(connStr, new MySqlServerVersion(new Version(8, 0, 44))));

// 3. JWT 认证
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    jwtKey = GenerateRandomKey(32);
    Console.WriteLine("警告：JWT 密钥未配置，已使用随机生成的临时密钥。请尽快在 appsettings.json 中设置固定密钥。");
}
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ChatRoom";
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            IssuerSigningKey = key
        };
        // SignalR 从查询字符串接收 token
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chatHub"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// 4. SignalR
builder.Services.AddSignalR()
    .AddJsonProtocol(opt =>
        opt.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// 5. CORS
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// 6. 后台服务（定时清理过期禁言）
builder.Services.AddHostedService<MuteCleanupService>();

// 7. 本地日志
var logDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Services.AddSingleton(new FileLogger(logDir));

var app = builder.Build();

// 中间件管道
app.UseForwardedHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

// 自动建表
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    db.Database.EnsureCreated();
}

// ==================== API 端点 ====================

// 注册
app.MapPost("/api/auth/register", async (HttpRequest request, ChatDbContext db) =>
{
    var body = await request.ReadFromJsonAsync<RegisterRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest("用户名和密码不能为空");
    if (await db.Users.AnyAsync(u => u.Username == body.Username))
        return Results.Conflict("用户名已存在");

    var user = new User
    {
        Username = body.Username,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.Password),
        SecurityQuestion = body.SecurityQuestion,
        SecurityAnswerHash = !string.IsNullOrWhiteSpace(body.SecurityAnswer)
            ? BCrypt.Net.BCrypt.HashPassword(body.SecurityAnswer)
            : null
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "注册成功" });
});

// 登录
app.MapPost("/api/auth/login", async (HttpRequest request, ChatDbContext db) =>
{
    var body = await request.ReadFromJsonAsync<LoginRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest("用户名和密码不能为空");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == body.Username);
    if (user == null || !BCrypt.Net.BCrypt.Verify(body.Password, user.PasswordHash))
        return Results.Unauthorized();
    if (user.IsBanned)
        return Results.BadRequest("您的账号已被封禁，无法登录");

    var claims = new[] { new Claim(ClaimTypes.Name, user.Username) };
    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        claims: claims,
        expires: DateTime.UtcNow.AddHours(24),
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
});

// 文件上传
app.MapPost("/api/upload", async (IFormFile file, IWebHostEnvironment env) =>
{
    if (file == null || file.Length == 0) return Results.BadRequest("没有选择文件");
    if (file.Length > 2048L * 1024 * 1024) return Results.BadRequest("文件不能超过 2GB");

    var webRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    var uploads = Path.Combine(webRoot, "uploads");
    Directory.CreateDirectory(uploads);
    var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
    var filePath = Path.Combine(uploads, fileName);
    using var stream = new FileStream(filePath, FileMode.Create);
    await file.CopyToAsync(stream);
    return Results.Ok(new { url = $"/uploads/{fileName}", fileName = file.FileName });
}).DisableAntiforgery();

// 找回密码 - 获取安全问题
app.MapGet("/api/auth/security-question", async (string username, ChatDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
    if (user == null || string.IsNullOrEmpty(user.SecurityQuestion))
        return Results.NotFound("用户不存在或未设置安全问题");
    return Results.Ok(new { question = user.SecurityQuestion });
});

// 找回密码 - 重置密码
app.MapPost("/api/auth/reset-password", async (HttpRequest request, ChatDbContext db) =>
{
    var body = await request.ReadFromJsonAsync<ResetPasswordRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Answer) || string.IsNullOrWhiteSpace(body.NewPassword))
        return Results.BadRequest("所有字段必须填写");
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == body.Username);
    if (user == null || string.IsNullOrEmpty(user.SecurityAnswerHash))
        return Results.BadRequest("用户未设置安全问题");
    if (!BCrypt.Net.BCrypt.Verify(body.Answer, user.SecurityAnswerHash))
        return Results.BadRequest("安全问题答案错误");
    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.NewPassword);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "密码重置成功，请登录" });
});

// ==================== 管理 API ====================

// 状态
app.MapGet("/api/admin/status", (IHostApplicationLifetime lifetime) =>
    Results.Ok(new { IsRunning = !lifetime.ApplicationStopping.IsCancellationRequested }));

// 在线用户
app.MapGet("/api/admin/onlineUsers", () => Results.Ok(ChatHub.GetOnlineUsers()));

// 日志
app.MapGet("/api/admin/logs", () =>
{
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "chatlog.jsonl");
    if (!File.Exists(logPath)) return Results.Ok(Array.Empty<string>());
    var lines = File.ReadLines(logPath).Reverse().Take(100).Reverse();
    return Results.Ok(lines);
});

// 播放列表查看
app.MapGet("/api/admin/playlist", async (ChatDbContext db) =>
    Results.Ok(await db.Playlists.OrderBy(p => p.SortOrder).ToListAsync()));

// 播放列表删除
app.MapDelete("/api/admin/playlist/{id:int}", async (int id, ChatDbContext db, IHubContext<ChatHub> chatHub) =>
{
    var item = await db.Playlists.FindAsync(id);
    if (item == null) return Results.NotFound();
    db.Playlists.Remove(item);
    await db.SaveChangesAsync();
    var playlist = await db.Playlists.OrderBy(p => p.SortOrder).ToListAsync();
    await chatHub.Clients.All.SendAsync("PlaylistUpdated", playlist);
    return Results.Ok();
});

// 清空消息
app.MapDelete("/api/admin/messages", async (HttpContext context, ChatDbContext db) =>
{
    var username = context.User?.Identity?.Name;
    if (username != "admin") return Results.Forbid();
    var count = await db.Messages.CountAsync();
    if (count == 0) return Results.Ok(new { message = "没有消息需要删除" });
    await db.Database.ExecuteSqlRawAsync("DELETE FROM Messages");
    return Results.Ok(new { message = $"成功删除 {count} 条消息" });
}).RequireAuthorization();

// 公告获取与设置
app.MapGet("/api/admin/announcement", async (ChatDbContext db) =>
{
    var announcement = await db.Announcements.OrderByDescending(a => a.Id).FirstOrDefaultAsync();
    return Results.Ok(announcement?.Content ?? "");
});

app.MapPost("/api/admin/announcement", async (HttpRequest request, ChatDbContext db, IHubContext<ChatHub> chatHub) =>
{
    var body = await request.ReadFromJsonAsync<AnnouncementRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest("内容不能为空");

    var announcement = new Announcement { Content = body.Content };
    db.Announcements.Add(announcement);
    await db.SaveChangesAsync();

    await chatHub.Clients.All.SendAsync("AdminAnnouncementUpdated", body.Content);
    return Results.Ok(new { message = "公告已更新" });
}).RequireAuthorization();

// ==================== SignalR Hub 映射 ====================
app.MapHub<ChatHub>("/chatHub");
app.MapHub<AdminHub>("/adminHub");

// 监听地址（从配置文件读取）
var host = builder.Configuration["ListenHost"] ?? "0.0.0.0";
var port = builder.Configuration.GetValue<int>("ListenPort", 25565);
app.Urls.Add($"http://{host}:{port}");

app.Run();

// ==================== 辅助方法 ====================

// 生成随机密钥（字母数字混合，长度建议32以上）
static string GenerateRandomKey(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    var random = new Random();
    return new string(Enumerable.Repeat(chars, length)
        .Select(s => s[random.Next(s.Length)]).ToArray());
}

// 确保配置文件存在，否则自动生成
static void EnsureConfigFileExists(string[] args)
{
    string configPath = "appsettings.json";
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
        {
            configPath = args[i + 1];
            break;
        }
    }

    if (File.Exists(configPath)) return;

    Console.WriteLine("未找到配置文件，正在生成默认 appsettings.json...");

    var defaultConfig = new
    {
        ListenHost = "127.0.0.1",
        ListenPort = 25565,
        ConnectionStrings = new
        {
            DefaultConnection = "server=127.0.0.1;port=3306;database=ChatRoomDb;user=chatroom;password=your_password;CharSet=utf8mb4"
        },
        AllowedOrigins = new[]
        {
            "可以不写但是一定要在nginx配置好",
        },
        Jwt = new
        {
            Key = GenerateRandomKey(32),
            Issuer = "ChatRoom"
        }
    };

    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    string json = JsonSerializer.Serialize(defaultConfig, jsonOptions);
    File.WriteAllText(configPath, json, Encoding.UTF8);

    Console.WriteLine($"默认配置文件已生成：{Path.GetFullPath(configPath)}");
    Console.WriteLine("请修改其中的数据库连接字符串和 JWT 密钥后重新启动服务。");
}

// ==================== DTO 记录 ====================
record RegisterRequest(string Username, string Password, string? SecurityQuestion = null, string? SecurityAnswer = null);
record LoginRequest(string Username, string Password);
record ResetPasswordRequest(string Username, string Answer, string NewPassword);
record AnnouncementRequest(string Content);