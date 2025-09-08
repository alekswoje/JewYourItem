using System;
using Newtonsoft.Json;

namespace JewYourItem;

public class RecentItem
{
    public string Name { get; set; }
    public string Price { get; set; }
    public string HideoutToken { get; set; }
    public string ItemId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public DateTime AddedTime { get; set; }
    public string Status { get; set; } = "Active"; // Active, NotFound, BadRequest, ServiceUnavailable
    public DateTime TokenIssuedAt { get; set; }
    public DateTime TokenExpiresAt { get; set; }

    public bool IsTokenExpired()
    {
        return DateTime.Now >= TokenExpiresAt.AddSeconds(-30); // 30 second buffer before actual expiration
    }

    public override string ToString()
    {
        return $"{Name} - {Price} at ({X}, {Y})";
    }

    public static (DateTime issuedAt, DateTime expiresAt) ParseTokenTimes(string token)
    {
        try
        {
            if (string.IsNullOrEmpty(token)) return (DateTime.MinValue, DateTime.MinValue);
            var parts = token.Split('.');
            if (parts.Length < 2) return (DateTime.MinValue, DateTime.MinValue);
            var payload = parts[1];
            while (payload.Length % 4 != 0) payload += "=";
            var bytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            dynamic tokenData = JsonConvert.DeserializeObject(json);
            long iat = tokenData?.iat ?? 0;
            long exp = tokenData?.exp ?? 0;
            var issuedAt = iat > 0 ? DateTimeOffset.FromUnixTimeSeconds(iat).DateTime : DateTime.MinValue;
            var expiresAt = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).DateTime : DateTime.MinValue;
            return (issuedAt, expiresAt);
        }
        catch
        {
            return (DateTime.MinValue, DateTime.MinValue);
        }
    }
}
