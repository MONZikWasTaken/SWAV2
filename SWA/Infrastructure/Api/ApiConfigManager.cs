using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SWA.Core.Models;
using SWA.Infrastructure.Resources;

namespace SWA.Infrastructure.Api
{
    public class ApiConfigManager
    {
        private static readonly HttpClient client = new HttpClient();
        private static readonly string configUrl = "";//https://pastebin.com/raw/fruEXg6E
        private static ApiConfiguration _config;

        public static ApiConfiguration Config => _config;

        public static async Task<ApiConfiguration> LoadConfigurationAsync()
        {
            try
            {
                // Try to load configuration from URL
                string json = await client.GetStringAsync(configUrl);
                _config = JsonConvert.DeserializeObject<ApiConfiguration>(json);

                // Log the loaded configuration
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loaded API configuration from {configUrl}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: API URL: {_config.Api}{Environment.NewLine}");

                return _config;
            }
            catch (Exception ex)
            {
                // Log error
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading API configuration: {ex.Message}{Environment.NewLine}");

                // Return default configuration as fallback
                _config = new ApiConfiguration
                {
                    Api = "https://api.swa-recloud.fun/",
                    LoginApi = "http://swacloud.com/",
                    UpdateUrl = "https://api.swa-recloud.fun/api/v3/get/latest.exe",
                    SteamUrl = "https://api.swa-recloud.fun/api/v3/get/SWAV2_installer.zip",
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
                        Heartbeat = "/api/v3/heartbeat",
                        Banner = "/api/v3/Banner/"
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
                // Use embedded resources - create temp file from embedded HTML
                try
                {
                    // Extract filename from resource path (e.g., "/login" -> "login.html")
                    string fileName = resource.TrimStart('/') + ".html";
                    return ResourceHelper.CreateTempHtmlFile(fileName);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error loading embedded UI resource '{resource}': {ex.Message}{Environment.NewLine}");

                    // Fallback to old external UI folder method
                    string appPath = System.Windows.Forms.Application.StartupPath;
                    return Path.Combine(appPath, "UI", resource);
                }
            }
            else
            {
                // Use remote UI
                return $"{_config.Api.TrimEnd('/')}{resource}";
            }
        }
    }
}
