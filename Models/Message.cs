namespace ChatRoom2.Models;

public class Message
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string MessageType { get; set; } = "text";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
public string? OriginalFileName { get; set; }
}