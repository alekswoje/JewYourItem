using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JewYourItem.Utility;

public class RateLimiter
{
    private readonly Dictionary<string, RateLimitState> _rateLimits = new();
    private readonly object _lock = new();
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;

    public RateLimiter(Action<string> logMessage, Action<string> logError)
    {
        _logMessage = logMessage;
        _logError = logError;
    }

    public class RateLimitState
    {
        public int Hits { get; set; }
        public int Max { get; set; }
        public DateTime ResetTime { get; set; }
        public int Period { get; set; }
        public int Penalty { get; set; }
        public int SafeMax { get; set; }
        public int EmergencyThreshold { get; set; }
        public DateTime LastRequestTime { get; set; }
        public int RequestCount { get; set; }
        public int BurstCount { get; set; }
    }

    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
            // Parse X-Rate-Limit-Rules header
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rulesHeader))
            {
                var rules = string.Join(",", rulesHeader).Split(',');
                foreach (var rule in rules)
                {
                    var parts = rule.Trim().Split(':');
                    if (parts.Length >= 4)
                    {
                        var scope = parts[0];
                        if (int.TryParse(parts[1], out var hits) &&
                            int.TryParse(parts[2], out var period) &&
                            int.TryParse(parts[3], out var penalty))
                        {
                            lock (_lock)
                            {
                                _rateLimits[scope] = new RateLimitState
                                {
                                    Max = hits,
                                    Period = period,
                                    Penalty = penalty,
                                    ResetTime = DateTime.Now.AddSeconds(period),
                                    SafeMax = Math.Max(1, hits / 2), // Use only 50% of actual limit
                                    EmergencyThreshold = Math.Max(1, (int)(hits * 0.4)), // Emergency brake at 40%
                                    LastRequestTime = DateTime.Now,
                                    RequestCount = 0,
                                    BurstCount = 0
                                };
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account", out var accountHeader))
            {
                var accountData = string.Join(",", accountHeader).Split(',');
                foreach (var data in accountData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        var scope = "account";
                        if (int.TryParse(parts[0], out var hits) &&
                            int.TryParse(parts[1], out var max) &&
                            int.TryParse(parts[2], out var remaining))
                        {
                            lock (_lock)
                            {
                                if (_rateLimits.ContainsKey(scope))
                                {
                                    _rateLimits[scope].Hits = hits;
                                    _rateLimits[scope].Max = max;
                                }
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account-State header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account-State", out var stateHeader))
            {
                var state = string.Join(",", stateHeader);
                _logMessage($"Rate limit state: {state}");
            }

            // Parse X-Rate-Limit-Account-Max header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account-Max", out var maxHeader))
            {
                var max = string.Join(",", maxHeader);
                _logMessage($"Rate limit max: {max}");
            }
        }
        catch (Exception ex)
        {
            _logError($"Error parsing rate limit headers: {ex.Message}");
        }
    }

    public async Task<bool> CheckAndWaitIfNeeded(string scope = "account")
    {
        var adaptiveDelay = GetAdaptiveDelay(scope);
        if (adaptiveDelay > 0)
        {
            _logMessage($"Adaptive delay: {scope} usage high, waiting {adaptiveDelay}ms...");
            await Task.Delay(adaptiveDelay);
        }

        RateLimitState state;
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return true; // No rate limit info, proceed

            state = _rateLimits[scope];
        }

        var usagePercentage = (double)state.Hits / state.Max;

        // If we're at 80% or more of the limit, wait
        if (usagePercentage >= 0.8)
        {
            var waitTime = CalculateWaitTime(state);
            if (waitTime > 0)
            {
                _logMessage($"Rate limit warning: {scope} at {usagePercentage:P1} ({state.Hits}/{state.Max}). Waiting {waitTime}ms...");
                await Task.Delay(waitTime);
                return true;
            }
        }

        return true;
    }

    public async Task<int> HandleRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Parse Retry-After header
            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                if (int.TryParse(retryAfterHeader.First(), out var retryAfterSeconds))
                {
                    var waitTime = retryAfterSeconds * 1000; // Convert to milliseconds
                    _logMessage($"Rate limited! Waiting {retryAfterSeconds} seconds before retry...");
                    await Task.Delay(waitTime);
                    return waitTime;
                }
            }
            else
            {
                // Fallback to 60 seconds if no Retry-After header
                _logMessage("Rate limited! No Retry-After header, waiting 60 seconds...");
                await Task.Delay(60000);
                return 60000;
            }
        }

        // Parse rate limit headers for future requests
        ParseRateLimitHeaders(response);
        return 0;
    }

    private int CalculateWaitTime(RateLimitState state)
    {
        var timeUntilReset = (state.ResetTime - DateTime.Now).TotalMilliseconds;
        if (timeUntilReset > 0)
        {
            // Wait until reset time, but cap at penalty time
            return Math.Min((int)timeUntilReset, state.Penalty * 1000);
        }
        return 0;
    }

    public void LogCurrentState()
    {
        lock (_lock)
        {
            foreach (var kvp in _rateLimits)
            {
                var state = kvp.Value;
                var usagePercentage = (double)state.Hits / state.Max;
                _logMessage($"Rate limit {kvp.Key}: {state.Hits}/{state.Max} ({usagePercentage:P1}) - Resets in {(state.ResetTime - DateTime.Now).TotalSeconds:F1}s");
            }
        }
    }

    public int GetAdaptiveDelay(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return 1000; // Default 1 second delay if no info

            var state = _rateLimits[scope];
            var now = DateTime.Now;
            
            // Update request tracking
            state.RequestCount++;
            if ((now - state.LastRequestTime).TotalSeconds < 1)
            {
                state.BurstCount++;
            }
            else
            {
                state.BurstCount = 1; // Reset burst count
            }
            state.LastRequestTime = now;

            // Calculate usage against our SAFE limit (50% of actual)
            var safeUsagePercentage = (double)state.Hits / state.SafeMax;
            var emergencyUsagePercentage = (double)state.Hits / state.EmergencyThreshold;
            var usagePercentage = (double)state.Hits / state.Max;

            // Calculate adaptive delay based on usage percentage
            if (usagePercentage >= 0.9)
                return 5000; // 5 seconds if at 90%+
            else if (usagePercentage >= 0.8)
                return 2000; // 2 seconds if at 80%+
            else if (usagePercentage >= 0.6)
                return 1000; // 1 second if at 60%+
            else if (usagePercentage >= 0.4)
                return 500;  // 0.5 seconds if at 40%+
            
            return 0; // No delay if under 40%
        }
    }
}

public class ConservativeRateLimiter
{
    private readonly Dictionary<string, ConservativeRateLimitState> _rateLimits = new();
    private readonly object _lock = new();
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;

    public ConservativeRateLimiter(Action<string> logMessage, Action<string> logError)
    {
        _logMessage = logMessage;
        _logError = logError;
    }

    public class ConservativeRateLimitState
    {
        public int Hits { get; set; }
        public int Max { get; set; }
        public DateTime ResetTime { get; set; }
        public int Period { get; set; }
        public int Penalty { get; set; }
        public int SafeMax { get; set; }
        public int EmergencyThreshold { get; set; }
        public DateTime LastRequestTime { get; set; }
        public int RequestCount { get; set; }
        public int BurstCount { get; set; }
        public int ConsecutiveHighUsage { get; set; }
    }

    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
            // Parse X-Rate-Limit-Rules header
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rulesHeader))
            {
                var rules = string.Join(",", rulesHeader).Split(',');
                foreach (var rule in rules)
                {
                    var parts = rule.Trim().Split(':');
                    if (parts.Length >= 4)
                    {
                        var scope = parts[0];
                        if (int.TryParse(parts[1], out var hits) &&
                            int.TryParse(parts[2], out var period) &&
                            int.TryParse(parts[3], out var penalty))
                        {
                            lock (_lock)
                            {
                                _rateLimits[scope] = new ConservativeRateLimitState
                                {
                                    Max = hits,
                                    Period = period,
                                    Penalty = penalty,
                                    ResetTime = DateTime.Now.AddSeconds(period),
                                    SafeMax = Math.Max(1, hits / 2), // Use only 50% of actual limit
                                    EmergencyThreshold = Math.Max(1, (int)(hits * 0.4)), // Emergency brake at 40%
                                    LastRequestTime = DateTime.Now,
                                    RequestCount = 0,
                                    BurstCount = 0,
                                    ConsecutiveHighUsage = 0
                                };
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account header
            if (response.Headers.TryGetValues("X-Rate-Limit-Account", out var accountHeader))
            {
                var accountData = string.Join(",", accountHeader).Split(',');
                foreach (var data in accountData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        var scope = "account";
                        if (int.TryParse(parts[0], out var hits) &&
                            int.TryParse(parts[1], out var max) &&
                            int.TryParse(parts[2], out var remaining))
                        {
                            lock (_lock)
                            {
                                if (_rateLimits.ContainsKey(scope))
                                {
                                    _rateLimits[scope].Hits = hits;
                                    _rateLimits[scope].Max = max;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"Error parsing rate limit headers: {ex.Message}");
        }
    }

    public async Task<bool> CheckAndWaitIfNeeded(string scope = "account")
    {
        var delay = GetConservativeDelay(scope);
        if (delay > 0)
        {
            _logMessage($"🛡️ CONSERVATIVE RATE LIMITING: {scope} - waiting {delay}ms for safety...");
            
            // DEBUG: Log detailed delay information
            lock (_lock)
            {
                if (_rateLimits.ContainsKey(scope))
                {
                    var state = _rateLimits[scope];
                    var safeUsagePercentage = (double)state.Hits / state.SafeMax;
                    var actualUsagePercentage = (double)state.Hits / state.Max;
                    _logMessage($"🔍 DEBUG: Delay reason - Safe usage: {safeUsagePercentage:P1} ({state.Hits}/{state.SafeMax}), Actual usage: {actualUsagePercentage:P1} ({state.Hits}/{state.Max})");
                    _logMessage($"🔍 DEBUG: Burst count: {state.BurstCount}, Consecutive high usage: {state.ConsecutiveHighUsage}");
                }
            }
            
            await Task.Delay(delay);
        }
        return true;
    }

    public async Task<int> HandleRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logMessage($"🚨 RATE LIMITED! Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            _logMessage($"🔍 DEBUG: Rate limit response headers: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(";", h.Value)}"))}");
            
            // Parse Retry-After header
            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                var retryAfterValue = string.Join(",", retryAfterHeader);
                _logMessage($"🔍 DEBUG: Retry-After header found: {retryAfterValue}");
                
                if (int.TryParse(retryAfterHeader.First(), out var retryAfterSeconds))
                {
                    var waitTime = retryAfterSeconds * 1000;
                    _logMessage($"🚨 RATE LIMITED! Waiting {retryAfterSeconds} seconds before retry...");
                    _logMessage($"🔍 DEBUG: Rate limit wait time: {waitTime}ms");
                    await Task.Delay(waitTime);
                    return waitTime;
                }
                else
                {
                    _logMessage($"🔍 DEBUG: Failed to parse Retry-After value: {retryAfterValue}");
                }
            }
            else
            {
                _logMessage("🚨 RATE LIMITED! No Retry-After header, waiting 60 seconds...");
                _logMessage("🔍 DEBUG: No Retry-After header found, using fallback 60-second delay");
                await Task.Delay(60000);
                return 60000;
            }
        }

        // Parse rate limit headers for future requests
        _logMessage($"🔍 DEBUG: Parsing rate limit headers from response {response.StatusCode}");
        ParseRateLimitHeaders(response);
        return 0;
    }

    private int GetConservativeDelay(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return 2000; // Default 2 second delay if no info

            var state = _rateLimits[scope];
            var now = DateTime.Now;
            
            // Update request tracking
            state.RequestCount++;
            if ((now - state.LastRequestTime).TotalSeconds < 1)
            {
                state.BurstCount++;
            }
            else
            {
                state.BurstCount = 1;
            }
            state.LastRequestTime = now;

            // Calculate usage against our SAFE limit (50% of actual)
            var safeUsagePercentage = (double)state.Hits / state.SafeMax;
            var emergencyUsagePercentage = (double)state.Hits / state.EmergencyThreshold;

            // EMERGENCY BRAKE: If we're approaching our emergency threshold (40% of actual limit)
            if (emergencyUsagePercentage >= 1.0)
            {
                state.ConsecutiveHighUsage++;
                _logMessage($"🚨 EMERGENCY BRAKE: {scope} at {emergencyUsagePercentage:P1} of emergency threshold! Waiting 15 seconds...");
                _logMessage($"🔍 DEBUG: Emergency brake triggered - Emergency threshold: {state.EmergencyThreshold}, Current hits: {state.Hits}, Max: {state.Max}");
                return 15000; // 15 seconds emergency brake
            }

            // BURST PROTECTION: If we're making too many requests too quickly
            if (state.BurstCount > 2) // More conservative than before
            {
                _logMessage($"⚠️ BURST PROTECTION: {scope} made {state.BurstCount} requests in 1 second. Waiting 5 seconds...");
                _logMessage($"🔍 DEBUG: Burst protection triggered - Last request time: {state.LastRequestTime:HH:mm:ss.fff}, Current time: {now:HH:mm:ss.fff}");
                return 5000; // 5 seconds burst protection
            }

            // CONSERVATIVE DELAYS: Much more aggressive than before
            if (safeUsagePercentage >= 0.7) // 70% of our 50% = 35% of actual limit
            {
                state.ConsecutiveHighUsage++;
                _logMessage($"🔍 DEBUG: Conservative delay 8s - Safe usage {safeUsagePercentage:P1} >= 70% (35% of actual limit)");
                return 8000; // 8 seconds if at 35% of actual limit
            }
            else if (safeUsagePercentage >= 0.5) // 50% of our 50% = 25% of actual limit
            {
                state.ConsecutiveHighUsage++;
                _logMessage($"🔍 DEBUG: Conservative delay 5s - Safe usage {safeUsagePercentage:P1} >= 50% (25% of actual limit)");
                return 5000; // 5 seconds if at 25% of actual limit
            }
            else if (safeUsagePercentage >= 0.3) // 30% of our 50% = 15% of actual limit
            {
                state.ConsecutiveHighUsage++;
                _logMessage($"🔍 DEBUG: Conservative delay 3s - Safe usage {safeUsagePercentage:P1} >= 30% (15% of actual limit)");
                return 3000; // 3 seconds if at 15% of actual limit
            }
            else if (safeUsagePercentage >= 0.1) // 10% of our 50% = 5% of actual limit
            {
                state.ConsecutiveHighUsage = 0; // Reset consecutive high usage
                _logMessage($"🔍 DEBUG: Conservative delay 1.5s - Safe usage {safeUsagePercentage:P1} >= 10% (5% of actual limit)");
                return 1500; // 1.5 seconds if at 5% of actual limit
            }
            
            // Reset consecutive high usage if we're in safe zone
            state.ConsecutiveHighUsage = 0;
            
            // MINIMUM DELAY: Always have a delay to be safe
            _logMessage($"🔍 DEBUG: Minimum delay 1s - Safe usage {safeUsagePercentage:P1} < 10% (5% of actual limit)");
            return 1000; // 1 second minimum delay
        }
    }

    public void LogCurrentState()
    {
        lock (_lock)
        {
            foreach (var kvp in _rateLimits)
            {
                var state = kvp.Value;
                var safeUsagePercentage = (double)state.Hits / state.SafeMax;
                var actualUsagePercentage = (double)state.Hits / state.Max;
                _logMessage($"🛡️ Rate limit {kvp.Key}: {state.Hits}/{state.Max} (Actual: {actualUsagePercentage:P1}, Safe: {safeUsagePercentage:P1}) - Resets in {(state.ResetTime - DateTime.Now).TotalSeconds:F1}s");
            }
        }
    }
}
