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
        // If TokenExpiresAt is MinValue (failed parsing), consider it expired
        if (TokenExpiresAt == DateTime.MinValue)
            return true;
            
        // Safely calculate expiration with 30 second buffer
        // Instead of subtracting from TokenExpiresAt, add to current time
        var currentTimePlus30 = DateTime.Now.AddSeconds(30);
        
        // Check for overflow in the addition (though very unlikely)
        if (currentTimePlus30 < DateTime.Now)
            return false; // If overflow occurred, assume not expired
            
        return currentTimePlus30 >= TokenExpiresAt;
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
            
            // Validate Unix timestamps to prevent DateTime overflow
            // Unix timestamp valid range: roughly 1970 to 3000 (0 to ~32 billion)
            DateTime issuedAt = DateTime.MinValue;
            DateTime expiresAt = DateTime.MinValue;
            
            if (iat > 0 && iat <= 32503680000) // Valid range check
            {
                try
                {
                    issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat).DateTime;
                }
                catch
                {
                    issuedAt = DateTime.MinValue;
                }
            }
            
            if (exp > 0 && exp <= 32503680000) // Valid range check
            {
                try
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).DateTime;
                }
                catch
                {
                    expiresAt = DateTime.MinValue;
                }
            }
            
            return (issuedAt, expiresAt);
        }
        catch
        {
            return (DateTime.MinValue, DateTime.MinValue);
        }
    }
}
