using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace SWA
{
    public class ApiConfigManager
    {
        private static readonly HttpClient client = new HttpClient();
        private static readonly string configUrl = "CONFIG";
        private static ApiConfiguration _config;

        public static ApiConfiguration Config => _config;

        public static async Task<ApiConfiguration> LoadConfigurationAsync()
        {
            try
            {

                string json = await client.GetStringAsync(configUrl);
                _config = JsonConvert.DeserializeObject<ApiConfiguration>(json);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loaded API configuration from {configUrl}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: API URL: {_config.Api}{Environment.NewLine}");

                return _config;
            }
            catch (Exception ex)
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading API configuration: {ex.Message}{Environment.NewLine}");

                _config = new ApiConfiguration
                {
                    Api = "API",
                    LoginApi = "API",
                    UpdateUrl = "API",
                    SteamUrl = "API",
                    Ui = new UiConfiguration
                    {
                        Local = 1,
                        Paths = new UiPaths
                        {
                            Login = "/login",
                            Dashboard = "/dashboard",
                            Css = "/css/",
                            Js = "/js/"
                        }
                    },
                    ApiEndpoints = new ApiEndpoints
                    {
                        GameFetch = "/api/v3/fetch/",
                        FileDownload = "/api/v3/file/",
                        GetFile = "/api/v3/get/",
                        PatchNotes = "/api/v3/patch_notes/",
                        Version = "/api/v3/version/",
                        Socials = "/api/v3/socials/",
                        Launchers = "/api/v3/launchers/",
                        Heartbeat = "/api/v3/heartbeat"
                    },
                    Maintenance = new MaintenanceInfo
                    {
                        Scheduled = "false",
                        StartTime = "",
                        EndTime = "",
                        Message = ""
                    },
                    Heartbeat = new HeartbeatConfig
                    {
                        Enabled = true,
                        IntervalMs = 60000,
                        EnabledForGuests = true
                    }
                };

                return _config;
            }
        }

        public static string GetUIPath(string resource)
        {
            if (_config == null)
            {
                throw new InvalidOperationException("Configuration not loaded. Call LoadConfigurationAsync first.");
            }

            if (_config.Ui.Local == 1)
            {

                string appPath = System.Windows.Forms.Application.StartupPath;
                return Path.Combine(appPath, "UI", resource);
            }
            else
            {

                return $"{_config.Api.TrimEnd('/')}{resource}";
            }
        }
    }

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