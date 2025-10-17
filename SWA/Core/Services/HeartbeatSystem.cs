using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using System.IO;
using System.Collections.Generic;
using SWA.Infrastructure.Api;
using SWA.Infrastructure.Http;

namespace SWA.Core.Services
{
    public class HeartbeatSystem
    {
        private static Timer heartbeatTimer;
        private int heartbeatIntervalMs = 60000; // Default interval of 1 minute
        private bool isEnabled = true;

        private string Username { get; set; }
        private string HardwareId { get; set; }
        private string _uniqueId;

        // Add a public property for UniqueId
        public string UniqueId
        {
            get { return _uniqueId; }
            private set { _uniqueId = value; }
        }

        private string HeartbeatUrl { get; set; }
        public string AppVersion { get; set; } = "Unknown"; // App version property
        private Dictionary<string, string> AdditionalParams { get; set; } = new Dictionary<string, string>();

        public HeartbeatSystem(string username, string hwid, string uniqueId, string appVersion = "Unknown")
        {
            Username = username;
            HardwareId = hwid;
            UniqueId = uniqueId;
            AppVersion = appVersion;

            // Log the unique ID
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: HeartbeatSystem initialized with unique_id: {uniqueId}{Environment.NewLine}");

            // Check if username indicates a guest user
            bool isGuest = username.StartsWith("Guest") || username == "Guest";

            // Use ApiConfigManager to get configuration
            if (ApiConfigManager.Config != null)
            {
                // Get heartbeat URL from config
                if (ApiConfigManager.Config.ApiEndpoints?.Heartbeat != null)
                {
                    // Use the configured heartbeat endpoint
                    HeartbeatUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.Heartbeat}";
                }
                else
                {
                    // Fallback to default endpoint
                    HeartbeatUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}/api/v3/heartbeat";
                }

                // Get heartbeat settings from config
                if (ApiConfigManager.Config.Heartbeat != null)
                {
                    // Check if heartbeat is enabled
                    isEnabled = ApiConfigManager.Config.Heartbeat.Enabled;

                    // Check if guest heartbeats are allowed when username is "Guest"
                    if (isGuest && !ApiConfigManager.Config.Heartbeat.EnabledForGuests)
                    {
                        isEnabled = false;
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat disabled for guest user{Environment.NewLine}");
                    }

                    // Get configured interval
                    heartbeatIntervalMs = ApiConfigManager.Config.Heartbeat.IntervalMs > 0
                        ? ApiConfigManager.Config.Heartbeat.IntervalMs
                        : 60000; // Use default if invalid

                    // Get any additional parameters
                    if (ApiConfigManager.Config.Heartbeat.AdditionalParams != null)
                    {
                        AdditionalParams = ApiConfigManager.Config.Heartbeat.AdditionalParams;
                    }
                }
            }
            else
            {
                // Fallback URL if config not loaded
                HeartbeatUrl = "https://api.swa-recloud.fun/api/v3/heartbeat";

                // Disable heartbeats for guests by default if no config
                if (isGuest)
                {
                    isEnabled = false;
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat disabled for guest user (no config){Environment.NewLine}");
                }
            }

            // Log heartbeat system initialization
            File.AppendAllText(@"C:\GFK\errorlog.txt",
                $"{DateTime.Now}: Heartbeat system initialized for user {username} with URL {HeartbeatUrl}, " +
                $"Enabled: {isEnabled}, Interval: {heartbeatIntervalMs}ms, Guest: {isGuest}{Environment.NewLine}");
        }

        public bool IsEnabled => isEnabled;

        public void Start()
        {
            if (!isEnabled)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system not started (disabled in config){Environment.NewLine}");
                return;
            }

            // Send initial heartbeat
            SendHeartbeatAsync().ConfigureAwait(false);

            // Set up timer for regular heartbeats
            heartbeatTimer = new Timer(heartbeatIntervalMs);
            heartbeatTimer.Elapsed += async (sender, e) => await SendHeartbeatAsync();
            heartbeatTimer.AutoReset = true;
            heartbeatTimer.Enabled = true;

            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system started with interval {heartbeatIntervalMs}ms{Environment.NewLine}");
        }

        public void Stop()
        {
            heartbeatTimer?.Stop();
            heartbeatTimer?.Dispose();
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system stopped{Environment.NewLine}");
        }

        private async Task SendHeartbeatAsync()
        {
            try
            {
                // Create base heartbeat data
                var heartbeatData = new Dictionary<string, object>
                {
                    ["username"] = Username,
                    ["hwid"] = HardwareId,
                    ["unique_id"] = UniqueId,
                    ["app_version"] = AppVersion,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Add any additional parameters from config
                foreach (var param in AdditionalParams)
                {
                    if (!heartbeatData.ContainsKey(param.Key))
                    {
                        heartbeatData.Add(param.Key, param.Value);
                    }
                }

                var json = JsonConvert.SerializeObject(heartbeatData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Log the exact JSON being sent (for debugging)
                // File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending heartbeat with unique_id {UniqueId}: {json}{Environment.NewLine}");

                var response = await HttpClientManager.PostAsync(HeartbeatUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                // Log heartbeat response (only in debug or on specific conditions to avoid excessive logging)
                if (responseContent.Contains("error") || !response.IsSuccessStatusCode)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat error: {responseContent}{Environment.NewLine}");
                }
                else
                {
                    // Optionally log successful heartbeats periodically (e.g., every 10th beat)
                    // This is commented out to avoid filling the log file
                    // File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat sent successfully{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error sending heartbeat: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}