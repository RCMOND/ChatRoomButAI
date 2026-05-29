using System.Text.Json.Serialization;

namespace ChatRoom2.Dtos;

public class MessageRecord
{
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
[JsonPropertyName("fileName")]   // 确保这一行存在
    public string? FileName { get; set; }  // 确
}