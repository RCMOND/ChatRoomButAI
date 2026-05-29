using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ChatRoom2.Data;
using ChatRoom2.Models;
using ChatRoom2.Dtos;
using ChatRoom2.Services;

namespace ChatRoom2.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly ChatDbContext _db;
    private readonly FileLogger _logger;
    private readonly IHubContext<AdminHub> _adminHubContext;

    // 在线用户：用户名 -> ConnectionId
    internal static readonly ConcurrentDictionary<string, string> OnlineUsers = new(StringComparer.Ordinal);
    // 用户IP字典
    internal static readonly ConcurrentDictionary<string, string> UserIps = new();

    // 全局音乐状态
    private static readonly object _musicLock = new();
    private static string? _currentMusicUrl;
    private static string? _currentMusicTitle;
    private static double _currentMusicTime;
    private static bool _isMusicPlaying;
    private static string _musicCycleMode = "list";

    public ChatHub(ChatDbContext db, FileLogger logger, IHubContext<AdminHub> adminHubContext)
    {
        _db = db;
        _logger = logger;
        _adminHubContext = adminHubContext;
    }

    // ==================== 连接与断开 ====================
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var clientIp = httpContext?.Connection.RemoteIpAddress?.ToString();

        // IP 黑名单检查（数据库）
        if (!string.IsNullOrEmpty(clientIp))
        {
            bool isBanned = await _db.BannedIps.AnyAsync(b => b.Ip == clientIp);
            if (isBanned)
            {
                Context.Abort();
                return;
            }
        }

        var username = Context.User?.Identity?.Name;
        if (string.IsNullOrEmpty(username))
        {
            Context.Abort();
            return;
        }

        // 账号封禁检查
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user != null && user.IsBanned)
        {
            Context.Abort();
            return;
        }

        // 重复登录处理
        if (OnlineUsers.TryGetValue(username, out var existingConnectionId))
        {
            if (existingConnectionId != Context.ConnectionId)
            {
                await Clients.Caller.SendAsync("Kickout", "您的账号已在别处登录");
                Context.Abort();
                return;
            }
        }

        // 记录 IP 和在线状态
        if (!string.IsNullOrEmpty(clientIp))
            UserIps[username] = clientIp;
        OnlineUsers[username] = Context.ConnectionId;

        // 保存并广播系统消息
        await SaveSystemMessageAsync(_db, $"{username} 加入了聊天室");
        await Clients.All.SendAsync("UserJoined", username, user?.Avatar ?? "");

        await base.OnConnectedAsync();
        await SendOnlineUsersUpdate();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Context.User?.Identity?.Name;
        if (!string.IsNullOrEmpty(username))
        {
            // 保存并广播系统消息
            await SaveSystemMessageAsync(_db, $"{username} 离开了聊天室");
            await Clients.All.SendAsync("UserLeft", username);

            OnlineUsers.TryRemove(username, out _);
            UserIps.TryRemove(username, out _);
            await SendOnlineUsersUpdate();
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ==================== 公共静态方法（供管理端调用） ====================
    public static IEnumerable<string> GetOnlineUsers() => OnlineUsers.Keys;
    public static string? GetConnectionIdByUser(string username)
    {
        OnlineUsers.TryGetValue(username, out var cid);
        return cid;
    }

    // ==================== 系统消息持久化 ====================
    public static async Task SaveSystemMessageAsync(ChatDbContext db, string text)
    {
        // 查找或创建系统用户
        var systemUser = await db.Users.FirstOrDefaultAsync(u => u.Username == "_system_");
        if (systemUser == null)
        {
            systemUser = new User
            {
                Username = "_system_",
                PasswordHash = "",
                Avatar = ""
            };
            db.Users.Add(systemUser);
            await db.SaveChangesAsync();
        }

        var msg = new Message
        {
            UserId = systemUser.Id,
            Content = text,
            MessageType = "text",
            Timestamp = DateTime.UtcNow
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
    }

    // ==================== 禁言管理（数据库持久化） ====================
    public static async Task<bool> IsUserMutedAsync(string username, ChatDbContext db)
    {
        return await db.MuteRecords.AnyAsync(m => m.Username == username && m.ExpiresAt > DateTime.UtcNow);
    }

    public static async Task MuteUserAsync(string username, int minutes, ChatDbContext db,
        IHubContext<AdminHub> adminHub, IHubContext<ChatHub> chatHub)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(minutes);
        var existing = await db.MuteRecords.FirstOrDefaultAsync(m => m.Username == username);
        if (existing != null)
            existing.ExpiresAt = expiresAt;
        else
            db.MuteRecords.Add(new MuteRecord { Username = username, ExpiresAt = expiresAt });
        await db.SaveChangesAsync();

        await adminHub.Clients.All.SendAsync("MuteUpdated", username, expiresAt);
        var cid = GetConnectionIdByUser(username);
        if (cid != null)
            await chatHub.Clients.Client(cid).SendAsync("Muted", $"您已被禁言至 {expiresAt.ToLocalTime():HH:mm:ss}");
    }

    // ==================== IP 封禁管理（数据库持久化） ====================
    public static async Task BanIpAsync(string ip, string? reason, ChatDbContext db, IHubContext<AdminHub> adminHub)
    {
        if (!await db.BannedIps.AnyAsync(b => b.Ip == ip))
        {
            db.BannedIps.Add(new BannedIp { Ip = ip, Reason = reason });
            await db.SaveChangesAsync();
        }
        await BroadcastBannedIps(adminHub, db);
    }

    public static async Task UnbanIpAsync(string ip, ChatDbContext db, IHubContext<AdminHub> adminHub)
    {
        var record = await db.BannedIps.FirstOrDefaultAsync(b => b.Ip == ip);
        if (record != null)
        {
            db.BannedIps.Remove(record);
            await db.SaveChangesAsync();
        }
        await BroadcastBannedIps(adminHub, db);
    }

    public static async Task<IEnumerable<object>> GetBannedIpsAsync(ChatDbContext db)
    {
        return await db.BannedIps.Select(b => new { b.Ip, b.Reason }).ToListAsync();
    }

    private static async Task BroadcastBannedIps(IHubContext<AdminHub> adminHub, ChatDbContext db)
    {
        var list = await GetBannedIpsAsync(db);
        await adminHub.Clients.All.SendAsync("BannedIpsUpdated", list);
    }

    // ==================== 公告管理 ====================
    public async Task<string?> GetAdminAnnouncement()
    {
        var announcement = await _db.Announcements
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();
        return announcement?.Content;
    }

    // ==================== 用户操作 ====================
    public async Task Join(string? avatar = null)
    {
        var username = Context.User?.Identity?.Name!;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user != null && !string.IsNullOrEmpty(avatar))
        {
            user.Avatar = avatar;
            await _db.SaveChangesAsync();
        }
        _logger.Log("system", "系统", $"{username} 加入了聊天室", DateTime.UtcNow);
        // 注意：系统消息已在 OnConnectedAsync 中持久化和广播，这里仅更新头像
        await Clients.All.SendAsync("UserJoined", username, user?.Avatar ?? "");
    }

    // ==================== 禁言检查 ====================
    private async Task CheckMuteAsync()
    {
        var username = Context.User?.Identity?.Name;
        if (!string.IsNullOrEmpty(username) && await IsUserMutedAsync(username, _db))
        {
            var record = await _db.MuteRecords.FirstOrDefaultAsync(m => m.Username == username && m.ExpiresAt > DateTime.UtcNow);
            var until = record!.ExpiresAt;
            await Clients.Caller.SendAsync("Muted", $"您已被禁言至 {until.ToLocalTime():HH:mm:ss}");
            throw new HubException("您已被禁言");
        }
    }

    // ==================== 发送消息 ====================
    public async Task SendMessage(string message)
    {
        await CheckMuteAsync();
        var username = Context.User?.Identity?.Name!;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        var msg = new Message { UserId = user.Id, Content = message, MessageType = "text", Timestamp = DateTime.UtcNow };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        _logger.Log("text", username, message, msg.Timestamp);
        await Clients.All.SendAsync("ReceiveMessage", username, message, "text", user.Avatar, null);
    }

    public async Task SendImage(string imageUrl)
    {
        await CheckMuteAsync();
        var username = Context.User?.Identity?.Name!;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        var msg = new Message { UserId = user.Id, Content = imageUrl, MessageType = "image", Timestamp = DateTime.UtcNow };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        _logger.Log("image", username, imageUrl, msg.Timestamp);
        await Clients.All.SendAsync("ReceiveMessage", username, imageUrl, "image", user.Avatar, null);
    }

    public async Task SendFile(string fileUrl, string? originalFileName = null)
    {
        await CheckMuteAsync();
        var username = Context.User?.Identity?.Name!;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        var msg = new Message
        {
            UserId = user.Id,
            Content = fileUrl,
            MessageType = "file",
            OriginalFileName = originalFileName,
            Timestamp = DateTime.UtcNow
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        _logger.Log("file", username, fileUrl, msg.Timestamp);
        await Clients.All.SendAsync("ReceiveMessage", username, fileUrl, "file", user.Avatar, originalFileName);
    }

    // ==================== 头像更新 ====================
    public async Task UpdateAvatar(string avatar)
    {
        var username = Context.User?.Identity?.Name!;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        user.Avatar = avatar;
        await _db.SaveChangesAsync();
        await Clients.All.SendAsync("AvatarChanged", username, avatar);
    }

    // ==================== 历史消息 ====================
    public async Task<List<MessageRecord>> GetRecentMessages()
    {
        return await _db.Messages
            .OrderByDescending(m => m.Timestamp).Take(50).OrderBy(m => m.Timestamp)
            .Select(m => new MessageRecord
            {
                User = m.User != null ? m.User.Username : (m.UserId == 0 ? "系统" : "未知"),
                Avatar = m.User != null ? m.User.Avatar : "",
                Text = m.Content,
                Type = m.MessageType,
                Time = m.Timestamp.ToString("HH:mm"),
                FileName = m.OriginalFileName
            }).AsNoTracking().ToListAsync();
    }

    // ==================== 全局音乐同步 ====================
    public Task<MusicState> GetMusicState()
    {
        lock (_musicLock)
        {
            return Task.FromResult(new MusicState
            {
                CurrentUrl = _currentMusicUrl,
                CurrentTitle = _currentMusicTitle,
                CurrentTime = _currentMusicTime,
                IsPlaying = _isMusicPlaying,
                CycleMode = _musicCycleMode
            });
        }
    }

    public async Task PlayMusic(string url, string title)
    {
        lock (_musicLock) { _currentMusicUrl = url; _currentMusicTitle = title; _currentMusicTime = 0; _isMusicPlaying = true; }
        await BroadcastMusicState();
    }

    public async Task PauseMusic()
    {
        lock (_musicLock) { _isMusicPlaying = false; }
        await BroadcastMusicState();
    }

    public async Task ResumeMusic()
    {
        lock (_musicLock) { _isMusicPlaying = true; }
        await BroadcastMusicState();
    }

    public async Task StopMusic()
    {
        lock (_musicLock) { _currentMusicUrl = null; _currentMusicTitle = null; _currentMusicTime = 0; _isMusicPlaying = false; }
        await BroadcastMusicState();
    }

    public async Task NextMusic()
    {
        var playlist = await _db.Playlists.OrderBy(p => p.SortOrder).ToListAsync();
        if (playlist.Count == 0) { await StopMusic(); return; }
        int currentIdx = -1;
        lock (_musicLock) { if (_currentMusicUrl != null) currentIdx = playlist.FindIndex(p => p.Url == _currentMusicUrl); }
        int nextIdx = _musicCycleMode == "single" ? (currentIdx >= 0 ? currentIdx : 0) : (currentIdx + 1) % playlist.Count;
        var nextTrack = playlist[nextIdx];
        lock (_musicLock) { _currentMusicUrl = nextTrack.Url; _currentMusicTitle = nextTrack.Title; _currentMusicTime = 0; _isMusicPlaying = true; }
        await BroadcastMusicState();
    }

    public async Task SetCycleMode(string mode)
    {
        lock (_musicLock) { _musicCycleMode = mode; }
        await BroadcastMusicState();
    }

    private async Task BroadcastMusicState()
    {
        MusicState state;
        lock (_musicLock)
        {
            state = new MusicState
            {
                CurrentUrl = _currentMusicUrl,
                CurrentTitle = _currentMusicTitle,
                CurrentTime = _currentMusicTime,
                IsPlaying = _isMusicPlaying,
                CycleMode = _musicCycleMode
            };
        }
        await Clients.All.SendAsync("MusicStateUpdated", state);
    }

    // ==================== 播放列表管理 ====================
    public async Task AddToPlaylist(string url, string title)
    {
        var exists = await _db.Playlists.AnyAsync(p => p.Url == url);
        if (!exists)
        {
            var item = new PlaylistItem { Url = url, Title = title, SortOrder = await _db.Playlists.CountAsync() };
            _db.Playlists.Add(item);
            await _db.SaveChangesAsync();
            await BroadcastPlaylist();

            lock (_musicLock)
            {
                if (_currentMusicUrl == null)
                {
                    _currentMusicUrl = url;
                    _currentMusicTitle = title;
                    _currentMusicTime = 0;
                    _isMusicPlaying = true;
                }
            }
            await BroadcastMusicState();
        }
    }

    public async Task<List<PlaylistItem>> GetPlaylist()
    {
        return await _db.Playlists.OrderBy(p => p.SortOrder).ToListAsync();
    }

    private async Task BroadcastPlaylist()
    {
        var playlist = await _db.Playlists.OrderBy(p => p.SortOrder).ToListAsync();
        await Clients.All.SendAsync("PlaylistUpdated", playlist);
    }

    // ==================== 管理端推送 ====================
    private async Task SendOnlineUsersUpdate()
    {
        await _adminHubContext.Clients.All.SendAsync("OnlineUsersUpdated", OnlineUsers.Keys);
    }
}

// ==================== 音乐状态 DTO ====================
public class MusicState
{
    public string? CurrentUrl { get; set; }
    public string? CurrentTitle { get; set; }
    public double CurrentTime { get; set; }
    public bool IsPlaying { get; set; }
    public string CycleMode { get; set; } = "list";
}