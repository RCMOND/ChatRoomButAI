namespace ChatRoom2.Models;
public class BannedIp
{
    public int Id { get; set; }
    public string Ip { get; set; } = string.Empty;
    public string? Reason { get; set; }
}