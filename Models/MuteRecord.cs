namespace ChatRoom2.Models;
public class MuteRecord
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}