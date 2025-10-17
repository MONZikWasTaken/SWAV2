using System.Collections.Generic;
using Newtonsoft.Json;

namespace SWA.Core.Models
{
    public class ApiConfiguration
    {
        [JsonProperty("api")]
        public string Api { get; set; }

        [JsonProperty("login_api")]
        public string LoginApi { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("update_url")]
        public string UpdateUrl { get; set; }

        [JsonProperty("steam_url")]
        public string SteamUrl { get; set; }

        [JsonProperty("ui")]
        public UiConfiguration Ui { get; set; }

        [JsonProperty("api_endpoints")]
        public ApiEndpoints ApiEndpoints { get; set; }

        [JsonProperty("maintenance")]
        public MaintenanceInfo Maintenance { get; set; }

        [JsonProperty("heartbeat")]
        public HeartbeatConfig Heartbeat { get; set; }
    }

    public class UiConfiguration
    {
        [JsonProperty("local")]
        public int Local { get; set; }

        [JsonProperty("paths")]
        public UiPaths Paths { get; set; }
    }

    public class UiPaths
    {
        [JsonProperty("login")]
        public string Login { get; set; }

        [JsonProperty("dashboard")]
        public string Dashboard { get; set; }

        [JsonProperty("css")]
        public string Css { get; set; }

        [JsonProperty("js")]
        public string Js { get; set; }
    }

    public class ApiEndpoints
    {
        [JsonProperty("game_fetch")]
        public string GameFetch { get; set; }

        [JsonProperty("file_download")]
        public string FileDownload { get; set; }

        [JsonProperty("get_file")]
        public string GetFile { get; set; }

        [JsonProperty("patch_notes")]
        public string PatchNotes { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("socials")]
        public string Socials { get; set; }

        [JsonProperty("launchers")]
        public string Launchers { get; set; }

        [JsonProperty("heartbeat")]
        public string Heartbeat { get; set; }

        [JsonProperty("banner")]
        public string Banner { get; set; }
    }

    public class MaintenanceInfo
    {
        [JsonProperty("scheduled")]
        public string Scheduled { get; set; }

        [JsonProperty("start_time")]
        public string StartTime { get; set; }

        [JsonProperty("end_time")]
        public string EndTime { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public class HeartbeatConfig
    {
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("interval_ms")]
        public int IntervalMs { get; set; } = 60000;

        [JsonProperty("enabled_for_guests")]
        public bool EnabledForGuests { get; set; } = true;

        [JsonProperty("additional_params")]
        public Dictionary<string, string> AdditionalParams { get; set; }
    }
}
