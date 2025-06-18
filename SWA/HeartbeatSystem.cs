using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using System.IO;
using System.Collections.Generic;

namespace SWA
{
    public class HeartbeatSystem
    {
        private static readonly HttpClient client = new HttpClient();
        private static Timer heartbeatTimer;
        private int heartbeatIntervalMs = 60000;
        private bool isEnabled = true;

        private string Username { get; set; }
        private string HardwareId { get; set; }
        private string _uniqueId;

        public string UniqueId
        {
            get { return _uniqueId; }
            private set { _uniqueId = value; }
        }

        private string HeartbeatUrl { get; set; }
        public string AppVersion { get; set; } = "Unknown";
        private Dictionary<string, string> AdditionalParams { get; set; } = new Dictionary<string, string>();

        public HeartbeatSystem(string username, string hwid, string uniqueId, string appVersion = "Unknown")
        {
            Username = username;
            HardwareId = hwid;
            UniqueId = uniqueId;
            AppVersion = appVersion;

            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: HeartbeatSystem initialized with unique_id: {uniqueId}{Environment.NewLine}");

            bool isGuest = username.StartsWith("Guest") || username == "Guest";

            if (ApiConfigManager.Config != null)
            {

                if (ApiConfigManager.Config.ApiEndpoints?.Heartbeat != null)
                {

                    HeartbeatUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.Heartbeat}";
                }
                else
                {

                    HeartbeatUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}/api/v3/heartbeat";
                }

                if (ApiConfigManager.Config.Heartbeat != null)
                {

                    isEnabled = ApiConfigManager.Config.Heartbeat.Enabled;

                    if (isGuest && !ApiConfigManager.Config.Heartbeat.EnabledForGuests)
                    {
                        isEnabled = false;
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat disabled for guest user{Environment.NewLine}");
                    }

                    heartbeatIntervalMs = ApiConfigManager.Config.Heartbeat.IntervalMs > 0
                        ? ApiConfigManager.Config.Heartbeat.IntervalMs
                        : 60000;

                    if (ApiConfigManager.Config.Heartbeat.AdditionalParams != null)
                    {
                        AdditionalParams = ApiConfigManager.Config.Heartbeat.AdditionalParams;
                    }
                }
            }
            else
            {

                HeartbeatUrl = "API";

                if (isGuest)
                {
                    isEnabled = false;
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat disabled for guest user (no config){Environment.NewLine}");
                }
            }

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

            SendHeartbeatAsync().ConfigureAwait(false);

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

                var heartbeatData = new Dictionary<string, object>
                {
                    ["username"] = Username,
                    ["hwid"] = HardwareId,
                    ["unique_id"] = UniqueId,
                    ["app_version"] = AppVersion,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                foreach (var param in AdditionalParams)
                {
                    if (!heartbeatData.ContainsKey(param.Key))
                    {
                        heartbeatData.Add(param.Key, param.Value);
                    }
                }

                var json = JsonConvert.SerializeObject(heartbeatData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(HeartbeatUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (responseContent.Contains("error") || !response.IsSuccessStatusCode)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat error: {responseContent}{Environment.NewLine}");
                }
                else
                {

                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error sending heartbeat: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}