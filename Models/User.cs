namespace ChatRoom2.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public List<Message> Messages { get; set; } = new();
    public string? SecurityQuestion { get; set; }      // 安全问题
    public string? SecurityAnswerHash { get; set; }    // 答案的哈希（BCrypt）
public bool IsBanned { get; set; } = false;
}