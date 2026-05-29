using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ChatRoom2.Data;
using ChatRoom2.Hubs;
using ChatRoom2.Models;
namespace ChatRoom2.Hubs;

 
[Authorize]
public class AdminHub : Hub
{
    private readonly ChatDbContext _db;
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly IHubContext<AdminHub> _adminHub;

    public AdminHub(ChatDbContext db, IHubContext<ChatHub> chatHub, IHubContext<AdminHub> adminHub)
    {
        _db = db;
        _chatHub = chatHub;
        _adminHub = adminHub;
    }

    public override async Task OnConnectedAsync()
    {
        var username = Context.User?.Identity?.Name;
        if (username != "admin")
        {
            await Clients.Caller.SendAsync("Kickout", "只有管理员才能连接管理端");
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }
public async Task SetAdminAnnouncement(string content)
{
   var announcement = new Announcement { Content = content };
    _db.Announcements.Add(announcement);
    await _db.SaveChangesAsync();

    // 实时推送（可选，前端已改为主动拉取，但可保留以支持实时更新）
    await _chatHub.Clients.All.SendAsync("AdminAnnouncementUpdated", content);
}
    // 踢出
    public async Task KickUser(string username)
    {
          var cid = ChatHub.GetConnectionIdByUser(username);
    if (cid != null)
        await _chatHub.Clients.Client(cid).SendAsync("Kickout", "您已被管理员踢出");

    // 保存系统消息
    await ChatHub.SaveSystemMessageAsync(_db, $"{username} 被管理员踢出");
    // 广播到聊天室
    await _chatHub.Clients.All.SendAsync("ReceiveMessage", "系统", $"{username} 被管理员踢出", "text", "", null);
    }

    // 禁言
    public async Task MuteUser(string username, int minutes)
    {
        await ChatHub.MuteUserAsync(username, minutes, _db, _adminHub, _chatHub);
    await ChatHub.SaveSystemMessageAsync(_db, $"{username} 被禁言 {minutes} 分钟");
    await _chatHub.Clients.All.SendAsync("ReceiveMessage", "系统", $"{username} 被禁言 {minutes} 分钟", "text", "", null);
    }

    // 封禁账号
    public async Task BanUser(string username)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        user.IsBanned = true;
        await _db.SaveChangesAsync();
        var cid = ChatHub.GetConnectionIdByUser(username);
        if (cid != null)
            await _chatHub.Clients.Client(cid).SendAsync("Kickout", "您已被封禁");
  await ChatHub.SaveSystemMessageAsync(_db, $"{username} 被管理员封禁");
    // 实时广播给所有聊天室用户
    await _chatHub.Clients.All.SendAsync("ReceiveMessage", "系统", $"{username} 被管理员封禁", "text", "", null);
    }

    // 解封账号
    public async Task UnbanUser(string username)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return;
        user.IsBanned = false;
        await _db.SaveChangesAsync();
    await ChatHub.SaveSystemMessageAsync(_db, $"{username} 被管理员解封");
    // 实时广播给所有聊天室用户
    await _chatHub.Clients.All.SendAsync("ReceiveMessage", "系统", $"{username} 被管理员解封", "text", "", null);
    }

    // 获取在线用户状态
public async Task<List<UserStatusDto>> GetOnlineUsersWithStatus()
{
    var onlineUsernames = ChatHub.OnlineUsers.Keys.ToList();
    if (onlineUsernames.Count == 0) return new List<UserStatusDto>();

    var now = DateTime.UtcNow;

    // 批量查询禁言用户名（一次查询）
    var mutedUsernames = await _db.MuteRecords
        .Where(m => m.ExpiresAt > now && onlineUsernames.Contains(m.Username))
        .Select(m => m.Username)
        .ToListAsync();

    // 批量查询被封禁用户名（一次查询）
    var bannedUsernames = await _db.Users
        .Where(u => onlineUsernames.Contains(u.Username) && u.IsBanned)
        .Select(u => u.Username)
        .ToListAsync();

    // 内存组装 DTO
    return onlineUsernames.Select(username => new UserStatusDto
    {
        Username = username,
        IsOnline = true,
        IsMuted = mutedUsernames.Contains(username),
        IsBanned = bannedUsernames.Contains(username)
    }).ToList();
}
    // 获取用户 IP
    public Task<string?> GetUserIp(string username)
    {
        ChatHub.UserIps.TryGetValue(username, out var ip);
        return Task.FromResult(ip);
    }

    // 封禁 IP
    public async Task BanIp(string ip, string? reason = null)
    {
        await ChatHub.BanIpAsync(ip, reason, _db, _adminHub);
    }

    // 解封 IP
    public async Task UnbanIp(string ip)
    {
        await ChatHub.UnbanIpAsync(ip, _db, _adminHub);
    }

    // 获取被封 IP 列表
    public async Task<IEnumerable<object>> GetBannedIpList()
    {
        return await ChatHub.GetBannedIpsAsync(_db);
    }

    // 获取所有被封禁的用户
    public async Task<List<BannedUserDto>> GetBannedUsers()
    {
        return await _db.Users.Where(u => u.IsBanned)
            .Select(u => new BannedUserDto { Username = u.Username, IsBanned = true })
            .ToListAsync();
    }
}

public class UserStatusDto
{
    public string Username { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsMuted { get; set; }
    public bool IsBanned { get; set; }
}

public class BannedUserDto
{
    public string Username { get; set; } = "";
    public bool IsBanned { get; set; }
}