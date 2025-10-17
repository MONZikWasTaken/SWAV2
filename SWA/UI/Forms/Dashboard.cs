using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;
using System.Threading;
using System.Net.Http;
using Newtonsoft.Json;
using SWA.Infrastructure.Api;
using SWA.Core.Services;
using SWA.Infrastructure.Resources;
using SWA.Infrastructure.Http;
using System.Security.Principal;

namespace SWA.UI.Forms
{
    public class GameCard
    {
        public string game_id { get; set; }
        public string game_name { get; set; }
        public int dlc_count { get; set; }
        public List<object> dlc { get; set; } = new List<object>();
    }

    public partial class Dashboard : Form
    {
        private WebView2 webView;
        private Label loadingLabel;
        private string apiUrl;
        private HeartbeatSystem heartbeatSystem;
        private FileSystemWatcher userDataWatcher;
        private FileSystemWatcher steamSettingsWatcher;
        private System.Threading.Timer userDataCheckTimer;
        private System.Threading.Timer restrictionCheckTimer;
        private FormWindowState lastWindowState = FormWindowState.Normal;

        private Dictionary<string, DateTime> gameInfoRequests = new Dictionary<string, DateTime>();
        private readonly object gameInfoLock = new object();

        public const string APP_VERSION = "v1.4.1";

        private static int steamPathCallCount = 0;  

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn
        (
            int nLeftRect,
            int nTopRect,
            int nRightRect,
            int nBottomRect,
            int nWidthEllipse,
            int nHeightEllipse
        );

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        public const int WM_NCCALCSIZE = 0x0083;

        private string selectedFilePath = null;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x20000;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // DISABLED: Rounded corners cause WebView2 to break on minimize/restore
            //if (m.Msg == WM_NCCALCSIZE && m.WParam.ToInt32() == 1)
            //{
            //    ApplyRoundedCorners();
            //}
        }

        private void ApplyRoundedCorners()
        {
            // DISABLED: Rounded corners cause WebView2 to break on minimize/restore
            //IntPtr region = CreateRoundRectRgn(0, 0, this.Width, this.Height, 20, 20);
            //SetWindowRgn(this.Handle, region, true);
        }

        private bool IsDebugMode()
        {
            try
            {
                string debugConfigPath = @"C:\GFK\debug.json";
                if (File.Exists(debugConfigPath))
                {
                    string json = File.ReadAllText(debugConfigPath);
                    dynamic config = JsonConvert.DeserializeObject(json);
                    return config?.debug == true;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error reading debug.json: {ex.Message}{Environment.NewLine}");
            }
            return false; // Default to non-debug mode
        }

        private bool ExecuteWithElevation(Action action, string operationName)
        {
            try
            {
                action();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Access denied during {operationName}, requesting UAC elevation...{Environment.NewLine}");

                if (!IsRunningAsAdministrator())
                {
                    try
                    {
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Verb = "runas",
                            UseShellExecute = true
                        };

                        Process.Start(startInfo);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restarted app with admin privileges for {operationName}{Environment.NewLine}");

                        Application.Exit();
                        return false;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User cancelled UAC or error: {ex.Message}{Environment.NewLine}");
                        return false;
                    }
                }
                return false;
            }
        }

        private async Task<bool> ExecuteWithElevationAsync(Func<Task> asyncAction, string operationName)
        {
            try
            {
                await asyncAction();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Access denied during {operationName}, requesting UAC elevation...{Environment.NewLine}");

                if (!IsRunningAsAdministrator())
                {
                    try
                    {
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Verb = "runas",
                            UseShellExecute = true
                        };

                        Process.Start(startInfo);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restarted app with admin privileges for {operationName}{Environment.NewLine}");

                        Application.Exit();
                        return false;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User cancelled UAC or error: {ex.Message}{Environment.NewLine}");
                        return false;
                    }
                }
                return false;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Block Ctrl+F
            if (keyData == (Keys.Control | Keys.F))
            {
                return true; // Block the key
            }

            // Block Ctrl+Shift+I (DevTools shortcut)
            if (keyData == (Keys.Control | Keys.Shift | Keys.I))
            {
                if (!IsDebugMode())
                {
                    return true; // Block if not in debug mode
                }
            }

            // F12 to open DevTools (only if debug mode enabled)
            if (keyData == Keys.F12)
            {
                if (IsDebugMode())
                {
                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: F12 pressed - Opening DevTools{Environment.NewLine}");
                            webView.CoreWebView2.OpenDevToolsWindow();
                            return true; // Key handled
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening DevTools via F12: {ex.Message}{Environment.NewLine}");
                    }
                }
                return true; // Key handled (blocked if not debug mode)
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        public Dashboard()
        {
            InitializeComponent();

            // Load icon from application directory
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading icon: {ex.Message}{Environment.NewLine}");
            }

            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "SWA V2 - Dashboard";
            this.BackColor = Color.FromArgb(18, 18, 18);
            this.KeyPreview = true; // Enable F12 keyboard shortcut

            // Create loading label
            loadingLabel = new Label
            {
                Text = "Loading...",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 14, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Visible = true
            };
            this.Controls.Add(loadingLabel);
            loadingLabel.BringToFront();

            InitializeWebViewAsync().ConfigureAwait(false);

            // DISABLED: Rounded corners cause WebView2 issues
            //this.Load += (s, e) => ApplyRoundedCorners();

            SetupUserDataMonitoring();
            SetupSteamSettingsMonitoring();
            StartSteamToolsMonitoring();

            this.FormClosing += Dashboard_FormClosing;

            this.Load += async (s, e) => await DownloadAndPatchSteamAsync();

// DISABLED:             StartRestrictionCheckTimer();
        }

        private void Dashboard_FormClosing(object sender, FormClosingEventArgs e)
        {

            try
            {


                heartbeatSystem?.Stop();

                if (userDataWatcher != null)
                {
                    userDataWatcher.EnableRaisingEvents = false;
                    userDataWatcher.Dispose();
                }

                if (steamSettingsWatcher != null)
                {
                    steamSettingsWatcher.EnableRaisingEvents = false;
                    steamSettingsWatcher.Dispose();
                }

                userDataCheckTimer?.Dispose();
                StopRestrictionCheckTimer();

            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error during application closing: {ex.Message}{Environment.NewLine}");
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {

                if (ApiConfigManager.Config == null)
                {
                    await ApiConfigManager.LoadConfigurationAsync();
                }

                isUserRestricted = false;
                restrictionReason = string.Empty;
                restrictionRedirectUrl = string.Empty;

                apiUrl = ApiConfigManager.Config.Api;
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Initializing Dashboard with API URL: {apiUrl}{Environment.NewLine}");

                this.Controls.Clear();

                webView = new WebView2();
                webView.Dock = DockStyle.Fill;
                webView.AllowExternalDrop = false;

                webView.Focus();

                this.Controls.Add(webView);

                // Hide loading label once WebView is ready
                if (loadingLabel != null)
                {
                    loadingLabel.Visible = false;
                }

                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Set dark background to prevent white flash
                webView.DefaultBackgroundColor = Color.FromArgb(18, 18, 18);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                // Enable DevTools only if debug mode is enabled
                webView.CoreWebView2.Settings.AreDevToolsEnabled = IsDebugMode();

                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                webView.ZoomFactor = 1.0;

                webView.ZoomFactorChanged += async (sender, evt) => {
                    if (webView.ZoomFactor != 1.0)
                    {
                        webView.ZoomFactor = 1.0;
                    }
                };

                webView.NavigationCompleted += (s, e) => {
                    webView.CoreWebView2.ExecuteScriptAsync(@"
                        document.addEventListener('wheel', function(e) {
                            if (e.ctrlKey) {
                                e.preventDefault();
                                return false;
                            }
                        }, { passive: false });

                        document.addEventListener('keydown', function(e) {
                            if (e.ctrlKey && (e.key === '+' || e.key === '-' || e.key === '0')) {
                                e.preventDefault();
                                return false;
                            }
                        });
                    ");
                };

                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                if (await CheckForUserRestrictions())
                {
                    LoadRestrictionPage();
                }
                else
                {

                    await LoadDashboardAsync();
                }

                webView.GotFocus += (s, e) => {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WebView got focus{Environment.NewLine}");
                };

                webView.LostFocus += (s, e) => {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WebView lost focus{Environment.NewLine}");
                };

                this.Activated += (s, e) => {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Form activated{Environment.NewLine}");
                    webView.Focus();
                };
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error initializing WebView2: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
                MessageBox.Show($"Error initializing dashboard: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool isUserRestricted = false;
        private string restrictionReason = string.Empty;
        private string restrictionRedirectUrl = string.Empty;

        private async Task<bool> CheckForUserRestrictions()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for user restrictions...{Environment.NewLine}");

                string hwid = string.Empty;
                string uniqueId = string.Empty;
                string username = string.Empty;

                string userDataPath = @"C:\GFK\user_data.json";
                if (File.Exists(userDataPath))
                {
                    try
                    {
                        string json = File.ReadAllText(userDataPath);
                        dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

                        hwid = userData?.hwid ?? userData?.device_id ?? GetDeviceId();
                        uniqueId = userData?.unique_id ?? string.Empty;
                        username = userData?.username ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error reading user data for restriction check: {ex.Message}{Environment.NewLine}");

                        hwid = GetDeviceId();
                    }
                }
                else
                {

                    hwid = GetDeviceId();
                }

                string restrictionUrl = $"{apiUrl}/api/v3/restriction";

                var requestBody = new Dictionary<string, string>();

                if (!string.IsNullOrEmpty(hwid))
                {
                    requestBody["hwid"] = hwid;
                }

                if (!string.IsNullOrEmpty(uniqueId))
                {

                    requestBody["unique_id"] = uniqueId;
                    requestBody["account_id"] = uniqueId;
                }

                if (!string.IsNullOrEmpty(username))
                {
                    requestBody["username"] = username;
                }

                string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
                var content = new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                var response = await HttpClientManager.PostAsync(restrictionUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = Newtonsoft.Json.JsonConvert.DeserializeObject(responseContent);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restriction check response: {responseContent}{Environment.NewLine}");

                    if (result.is_restricted == true)
                    {
                        isUserRestricted = true;

                        if (result.hwid_banned == true)
                        {
                            restrictionReason = result.hwid_reason ?? "Your device has been blocked.";
                            restrictionRedirectUrl = result.hwid_block_url ?? "";
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User is hardware banned: {restrictionReason}{Environment.NewLine}");
                        }
                        else if (result.account_banned == true || result.unique_id_banned == true)
                        {

                            if (result.account_banned == true)
                            {
                                restrictionReason = result.account_reason ?? "Your account has been suspended.";
                                restrictionRedirectUrl = result.account_block_url ?? "";
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User account is banned: {restrictionReason}{Environment.NewLine}");
                            }
                            else
                            {
                                restrictionReason = "Your account has been suspended.";
                                restrictionRedirectUrl = result.unique_id_url ?? "";
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User unique_id is banned{Environment.NewLine}");
                            }
                        }
                        else if (result.ip_banned == true)
                        {
                            restrictionReason = result.ip_reason ?? "Your IP address has been blocked.";
                            restrictionRedirectUrl = result.ip_block_url ?? "";
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User IP is banned: {restrictionReason}{Environment.NewLine}");
                        }
                        else
                        {
                            restrictionReason = "Access denied. Please contact support.";
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User is restricted for unknown reason{Environment.NewLine}");
                        }

                        return true;
                    }

                    if (!string.IsNullOrEmpty(uniqueId) && result.account_banned != true && result.unique_id_banned != true)
                    {
                        bool isAccountBanned = await CheckIfAccountIsBanned(uniqueId);
                        if (isAccountBanned)
                        {
                            isUserRestricted = true;
                            restrictionReason = "Your account has been suspended.";
                            restrictionRedirectUrl = $"/account_frozen.html?account_id={uniqueId}&reason=Account%20suspended";
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User account (unique_id) is banned: {uniqueId}{Environment.NewLine}");
                            return true;
                        }
                    }

                    return false;
                }
                else
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking restrictions: {response.StatusCode}{Environment.NewLine}");
                    return false;
                }
            }
            catch (Exception ex)
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception in restriction check: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private async Task<bool> CheckIfAccountIsBanned(string accountId)
        {
            try
            {
                if (string.IsNullOrEmpty(accountId))
                {
                    return false;
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Manually checking if account ID {accountId} is banned{Environment.NewLine}");

                var knownBlockedAccounts = new List<string>
                {

                };

                bool isBlocked = knownBlockedAccounts.Contains(accountId);

                if (isBlocked)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Found account {accountId} in known blocked accounts list{Environment.NewLine}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error checking if account is banned: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private void LoadRestrictionPage()
        {
            try
            {
                string redirectUrl;

                if (!string.IsNullOrEmpty(restrictionRedirectUrl))
                {

                    if (restrictionRedirectUrl.Contains("?"))
                    {
                        redirectUrl = $"{apiUrl}{restrictionRedirectUrl}&from=dashboard";
                    }
                    else
                    {
                        redirectUrl = $"{apiUrl}{restrictionRedirectUrl}?from=dashboard";
                    }
                }
                else
                {

                    redirectUrl = $"{apiUrl}/access_denied.html?reason={System.Net.WebUtility.UrlEncode(restrictionReason)}&from=dashboard";
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading restriction page: {redirectUrl}{Environment.NewLine}");

                if (webView?.CoreWebView2 != null)
                {

                    webView.CoreWebView2.WebMessageReceived -= RestrictionWebMessageHandler;
                    webView.CoreWebView2.WebMessageReceived += RestrictionWebMessageHandler;
                }

                webView.CoreWebView2.Navigate(redirectUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading restriction page: {ex.Message}{Environment.NewLine}");

                try
                {
                    webView.CoreWebView2.Navigate($"about:blank");
                    webView.CoreWebView2.NavigationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess)
                        {
                            webView.CoreWebView2.ExecuteScriptAsync($@"
                                document.body.innerHTML = '<div style=""font-family: Arial, sans-serif; padding: 20px; text-align: center;"">' +
                                '<h2 style=""color: #e74c3c;"">Access Restricted</h2>' +
                                '<p>{restrictionReason.Replace("'", "\\'")}</p>' +
                                '<p>Please contact support for assistance.</p>' +
                                '</div>';
                                document.body.style.backgroundColor = '#222';
                                document.body.style.color = '#fff';
                            ");
                        }
                    };
                }
                catch
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Unable to show restriction page, closing application{Environment.NewLine}");
                    this.Close();
                }
            }
        }

        private void RestrictionWebMessageHandler(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Received message from restriction page: {message}{Environment.NewLine}");

                if (message == "close")
                {
                    this.Close();
                }
                else if (message == "minimize")
                {
                    this.WindowState = FormWindowState.Minimized;
                }
                else if (message == "open-devtools")
                {
                    // Only allow if debug mode is enabled
                    if (IsDebugMode())
                    {
                        try
                        {
                            if (webView != null && webView.CoreWebView2 != null)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Opening DevTools window{Environment.NewLine}");
                                webView.CoreWebView2.OpenDevToolsWindow();
                            }
                            else
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot open DevTools - WebView not initialized{Environment.NewLine}");
                            }
                        }
                        catch (Exception devToolsEx)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening DevTools: {devToolsEx.Message}{Environment.NewLine}");
                        }
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DevTools blocked - debug mode not enabled{Environment.NewLine}");
                    }
                }
                else if (message == "get-debug-mode")
                {
                    // Send debug mode status to frontend
                    bool debugMode = IsDebugMode();
                    string jsonResponse = $"{{\"type\":\"debug-mode\",\"enabled\":{debugMode.ToString().ToLower()}}}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending debug mode status: {jsonResponse}{Environment.NewLine}");
                    webView.CoreWebView2.PostWebMessageAsString(jsonResponse);
                }
                else if (message == "drag:start")
                {
                    ReleaseCapture();
                    SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                }
                else if (message.StartsWith("resize:"))
                {
                    try
                    {

                        string dimensions = message.Substring("resize:".Length);
                        string[] parts = dimensions.ToLower().Split('x');

                        if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
                        {

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Resize request from restriction page: {width}x{height}{Environment.NewLine}");

                            this.Size = new Size(width, height);

                            this.CenterToScreen();
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing resize message from restriction page: {ex.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error handling restriction page message: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task LoadDashboardAsync()
        {
            try
            {
                webView.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                webView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

                try
                {
                    webView.CoreWebView2.Stop();
                }
                catch (Exception stopEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error stopping previous navigation: {stopEx.Message}{Environment.NewLine}");
                }

                System.Threading.Timer loadingTimeoutTimer = null;
                loadingTimeoutTimer = new System.Threading.Timer((state) =>
                {
                    try
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading timeout reached, force hiding loading screen{Environment.NewLine}");
                        this.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                webView.CoreWebView2.ExecuteScriptAsync(@"
                                    try {
                                        const overlay = document.getElementById('loading-overlay');
                                        if (overlay) {
                                            overlay.classList.add('hidden');
                                            overlay.style.display = 'none';
                                            console.log('Loading screen hidden by timeout');
                                        }
                                    } catch (error) {
                                        console.error('Error hiding loading screen:', error);
                                    }
                                ");
                            }
                            catch (Exception timerEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in loading timeout handler: {timerEx.Message}{Environment.NewLine}");
                            }
                        }));
                    }
                    catch { }
                    finally
                    {
                        loadingTimeoutTimer?.Dispose();
                    }
                }, null, 5000, Timeout.Infinite);

                // Load UI based on configuration
                if (ApiConfigManager.Config.Ui.Local == 1)
                {
                    // Use embedded UI resources - load directly to memory (INSTANT!)
                    string htmlContent = ResourceHelper.LoadEmbeddedHtmlContent("dashboard.html");
                    webView.CoreWebView2.NavigateToString(htmlContent);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loaded embedded dashboard to memory (instant){Environment.NewLine}");
                }
                else
                {
                    // Use remote UI
                    string dashboardUrl = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.Ui.Paths.Dashboard}";
                    webView.CoreWebView2.Navigate(new Uri(dashboardUrl).ToString());
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Navigating to remote dashboard URL: {dashboardUrl}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading dashboard: {ex.Message}{Environment.NewLine}");

                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            const overlay = document.getElementById('loading-overlay');
                            if (overlay) {
                                overlay.classList.add('hidden');
                                overlay.style.display = 'none';
                                console.log('Loading screen hidden due to load error');
                            }
                        } catch (error) {
                            console.error('Error hiding loading screen:', error);
                        }
                    ");
                }
                catch { }
            }
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Dashboard navigation completed. Success: {e.IsSuccess}, Error code: {e.WebErrorStatus}{Environment.NewLine}");

                try
                {

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            try {

                                if (typeof caches !== 'undefined') {
                                    caches.keys().then(function(names) {
                                        for (let name of names) caches.delete(name);
                                        console.log('Browser cache cleared on navigation completion');
                                    });
                                }

                                if (document && document.body) {
                                    document.body.style.zoom = '100.1%';
                                    setTimeout(() => { document.body.style.zoom = '100%'; }, 50);
                                }
                            } catch (error) {
                                console.error('Error clearing cache:', error);
                            }
                        ");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Невозможно очистить кэш: webView или CoreWebView2 равны null{Environment.NewLine}");
                    }
                }
                catch (Exception cacheEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error clearing cache: {cacheEx.Message}{Environment.NewLine}");
                }

                // REMOVED: Don't hide loading screen immediately - let LoadUserData() complete first
                // Loading screen will be hidden by completeLoading() after all data is loaded

                if (e.IsSuccess)
                {

                    // Test critical endpoints first
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        (async function() {
                            const endpointsReachable = await testCriticalEndpoints();
                            if (!endpointsReachable) {
                                console.error('Critical endpoints are not reachable');
                                return;
                            }
                            if (typeof updateLoadingStatus === 'function') {
                                updateLoadingStatus('Initializing dashboard...', 10);
                            }
                        })();
                    ");

                    await System.Threading.Tasks.Task.Delay(800);

                    webView.Focus();

                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{

                            window.currentVersion = '{APP_VERSION}';

                            const versionBadge = document.getElementById('version-badge');
                            if (versionBadge) versionBadge.textContent = '{APP_VERSION}';

                            const currentVersionEl = document.getElementById('current-version');
                            if (currentVersionEl) currentVersionEl.textContent = '{APP_VERSION}';

                            console.log('Version initialized to: {APP_VERSION}');

                            if (typeof updateLoadingStatus === 'function') {{
                                updateLoadingStatus('Loading configuration...', 20);
                            }}
                        }} catch (error) {{
                            console.error('Error initializing version:', error);
                        }}
                    ");

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            console.log('Dashboard initialized successfully');
                            if (typeof updateLoadingStatus === 'function') {
                                updateLoadingStatus('Loading user data...', 30);
                            }
                        } catch (error) {
                            console.error('Error in simple initialization:', error);
                        }
                    ");

                    await Task.Delay(100);
                    LoadUserData();

                    await Task.Delay(100);
                    GetSteamInstallPath();

                    await Task.Delay(100);
                    InitializeSwaToggleState();

                    await Task.Delay(100);
                    // Auto-show update notification on startup if available
                    await CheckForUpdates(true);

                    await Task.Delay(100);
                    await GetPatchNotesAsync();

                    try
                    {
                        string userDataPath = @"C:\GFK\user_data.json";
                        if (File.Exists(userDataPath))
                        {
                            string json = File.ReadAllText(userDataPath);

                            if (json.Length > 1000000)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User data too large, skipping welcome message{Environment.NewLine}");
                            }
                            else
                            {
                                try
                                {
                                    dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                                    string username = userData?.username ?? "User";

                                    await UpdateLogMessage($"Welcome back, {username}!");
                                }
                                catch (Exception jsonEx)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error parsing user data JSON: {jsonEx.Message}{Environment.NewLine}");
                                    await UpdateLogMessage("Welcome to SWA V2!");
                                }
                            }
                        }
                        else
                        {
                            await UpdateLogMessage("Welcome to SWA V2!");
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error showing welcome message: {ex.Message}{Environment.NewLine}");
                    }

                    // REMOVED: Don't call completeLoading() here!
                    // Let processServerResponse() handle it after all data is loaded and validated
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Dashboard initialized, waiting for LoadUserData() to complete{Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Dashboard navigation failed with error: {e.WebErrorStatus}{Environment.NewLine}");

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            const overlay = document.getElementById('loading-overlay');
                            if (overlay) {
                                overlay.classList.add('hidden');
                                overlay.style.display = 'none';
                                console.log('Loading screen hidden despite navigation failure');
                            }

                            document.body.style.visibility = 'hidden';
                            setTimeout(() => { document.body.style.visibility = 'visible'; }, 50);
                        } catch (error) {
                            console.error('Error hiding loading screen:', error);
                        }
                    ");

                    await Task.Delay(2000);
                    try
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Attempting to reload dashboard after navigation failure{Environment.NewLine}");
                        await LoadDashboardAsync();
                    }
                    catch (Exception reloadEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Error reloading dashboard: {reloadEx.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Exception in WebView_NavigationCompleted: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            const overlay = document.getElementById('loading-overlay');
                            if (overlay) {
                                overlay.classList.add('hidden');
                                overlay.style.display = 'none';
                                console.log('Loading screen hidden after exception');
                            }
                        } catch (error) {
                            console.error('Error hiding loading screen:', error);
                        }
                    ");
                }
                catch { }
            }
        }

        private async void LoadUserData()
        {
            try
            {
                string userDataPath = @"C:\GFK\user_data.json";
                if (File.Exists(userDataPath))
                {
                    string json = File.ReadAllText(userDataPath);

                    dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

                    try
                    {
                        string username = userData?.username ?? "Guest";
                        string hwid = userData?.hwid ?? userData?.device_id ?? GetDeviceId();
                        string uniqueId = userData?.unique_id ?? $"guest_{GetDeviceId()}";

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using unique_id for heartbeat: {uniqueId}{Environment.NewLine}");

                        heartbeatSystem = new HeartbeatSystem(username, hwid, uniqueId, APP_VERSION);

                        if (heartbeatSystem.IsEnabled)
                        {
                            heartbeatSystem.Start();
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system started for user {username}{Environment.NewLine}");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system disabled for user {username}{Environment.NewLine}");
                        }
                    }
                    catch (Exception heartbeatEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error initializing heartbeat system: {heartbeatEx.Message}{Environment.NewLine}");
                    }

                    string statusDisplay = "Standard";
                    string expiryDisplay = "Never";

                    try
                    {
                        // Use status from API response (Admin, Premium, Standard, etc.)
                        if (userData.status != null)
                        {
                            statusDisplay = userData.status.ToString();
                        }

                        // Handle premium_expires_in_days
                        if (userData.premium_expires_in_days == null)
                        {
                            // Null = Lifetime
                            expiryDisplay = "Lifetime";
                        }
                        else
                        {
                            int premiumDays = Convert.ToInt32(userData.premium_expires_in_days);
                            if (premiumDays > 9000)
                            {
                                expiryDisplay = "Lifetime";
                            }
                            else if (premiumDays > 0)
                            {
                                expiryDisplay = $"{premiumDays} days";
                            }
                            else
                            {
                                expiryDisplay = "Expired";
                            }
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User status: {statusDisplay}, Expiry: {expiryDisplay}{Environment.NewLine}");
                    }
                    catch (Exception exPremium)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing user status/expiry: {exPremium.Message}{Environment.NewLine}");
                    }

                    try
                    {

                        string username = userData?.username ?? "User";
                        await UpdateLogMessage($"Welcome back, {username}!");

                        // Ensure device_id is in the userData
                        string currentDeviceId = GetDeviceId();
                        if (userData.device_id == null && userData.hwid == null)
                        {
                            userData.device_id = currentDeviceId;
                        }

                        // Re-serialize with device_id included
                        string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(userData);
                        string userDataJson = updatedJson.Replace("'", "\\'").Replace("\r\n", "").Replace("\n", "");

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                console.log('Loading user data...');

                                if (typeof updateLoadingStatus === 'function') {{
                                    updateLoadingStatus('Processing user data...', 50);
                                }}

                                // Process the full server response
                                const userData = {userDataJson};
                                console.log('User data:', userData);
                                console.log('Device ID in data:', userData.device_id || userData.hwid);

                                if (typeof updateLoadingStatus === 'function') {{
                                    updateLoadingStatus('Updating interface...', 70);
                                }}

                                // Fallback: Update UI elements directly
                                const username = document.querySelector('.username');
                                if (username) username.textContent = '{userData.username ?? "User"}';

                                // Call processServerResponse to handle all display logic
                                // This will also call completeLoading() when done
                                if (typeof processServerResponse === 'function') {{
                                    processServerResponse(userData);
                                }}

                                // Update plan badge
                                if (typeof updatePlanBadge === 'function') {{
                                    updatePlanBadge('{userData.status ?? "Standard"}');
                                }}

                                if (typeof updateLoadingStatus === 'function') {{
                                    updateLoadingStatus('Finalizing...', 85);
                                }}

                                console.log('User data loaded successfully');
                            }} catch (error) {{
                                console.error('Error updating user data:', error);

                                if (typeof completeLoading === 'function') {{
                                    completeLoading();
                                }}
                            }}
                        ");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error updating UI with user data: {ex.Message}{Environment.NewLine}");

                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            try {
                                if (typeof completeLoading === 'function') {
                                    completeLoading();
                                }
                            } catch (error) {
                                console.error('Error completing loading:', error);
                            }
                        ");
                    }

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Updated dashboard with user data{Environment.NewLine}");
                }
                else
                {

                    await UpdateLogMessage("Welcome to SWA V2!");

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            setTimeout(function() {
                                if (typeof completeLoading === 'function') {
                                    completeLoading();
                                    console.log('Guest loading completed');
                                }
                            }, 300);
                        } catch (error) {
                            console.error('Error completing loading:', error);
                            if (typeof completeLoading === 'function') {
                                completeLoading();
                            }
                        }
                    ");

                    try
                    {
                        string guestName = "Guest_" + DateTime.Now.Ticks.ToString().Substring(10);
                        string hwid = GetDeviceId();
                        string uniqueId = $"guest_{hwid}";

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using unique_id for guest heartbeat: {uniqueId}{Environment.NewLine}");

                        heartbeatSystem = new HeartbeatSystem(guestName, hwid, uniqueId, APP_VERSION);

                        if (heartbeatSystem.IsEnabled)
                        {
                            heartbeatSystem.Start();
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system started for guest user{Environment.NewLine}");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system disabled for guest users{Environment.NewLine}");
                        }
                    }
                    catch (Exception heartbeatEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error initializing heartbeat system for guest: {heartbeatEx.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading user data: {ex.Message}{Environment.NewLine}");

                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        if (typeof completeLoading === 'function') {
                            completeLoading();
                        }
                    ");
                }
                catch { /* Ignore errors in error handler */ }
            }
        }

        public async Task ProcessServerResponse(string serverResponse)
        {
            try
            {

                string jsonData = serverResponse;
                if (serverResponse.Contains("Server response:"))
                {
                    int jsonStart = serverResponse.IndexOf('{');
                    if (jsonStart >= 0)
                    {
                        jsonData = serverResponse.Substring(jsonStart);
                    }
                }

                try
                {
                    var responseData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(jsonData);

                    // Ensure device_id is always included
                    if (responseData != null)
                    {
                        string currentDeviceId = GetDeviceId();
                        if (responseData.device_id == null && responseData.hwid == null)
                        {
                            responseData.device_id = currentDeviceId;
                        }
                        // Update jsonData with the modified response
                        jsonData = Newtonsoft.Json.JsonConvert.SerializeObject(responseData);
                    }

                    File.WriteAllText(@"C:\GFK\user_data.json", jsonData);

                    if (heartbeatSystem != null && responseData.username != null)
                    {
                        string username = responseData.username;
                        string hwid = responseData.hwid ?? responseData.device_id ?? GetDeviceId();
                        string uniqueId = responseData.unique_id ?? heartbeatSystem.UniqueId;

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Updating heartbeat with unique_id: {uniqueId}{Environment.NewLine}");

                        heartbeatSystem.Stop();

                        heartbeatSystem = new HeartbeatSystem(username, hwid, uniqueId, APP_VERSION);

                        if (heartbeatSystem.IsEnabled)
                        {
                            heartbeatSystem.Start();
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system updated for user {username}{Environment.NewLine}");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Updated heartbeat system disabled for user {username}{Environment.NewLine}");
                        }
                    }

                    string premiumDisplay = "Standard";
                    try
                    {
                        if (responseData.premium_expires_in_days != null)
                        {
                            int premiumDays = Convert.ToInt32(responseData.premium_expires_in_days);
                            if (premiumDays > 9000)
                            {
                                premiumDisplay = "∞ Lifetime";
                            }
                            else if (premiumDays > 0)
                            {
                                premiumDisplay = $"{premiumDays} days";
                            }
                        }
                        else if (responseData.status != null && responseData.status.ToString().Contains("Premium"))
                        {
                            premiumDisplay = responseData.status;
                        }

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                const userPlan = document.querySelector('.user-plan');
                                if (userPlan) userPlan.textContent = '{premiumDisplay}';
                                console.log('Updated premium display to: {premiumDisplay}');
                            }} catch (error) {{
                                console.error('Error updating premium display:', error);
                            }}
                        ");
                    }
                    catch (Exception exPremium)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing premium days in response: {exPremium.Message}{Environment.NewLine}");
                    }

                    if (responseData.Response != null)
                    {
                        string responseMessage = responseData.Response.ToString();
                        string responseType = "success";

                        if (responseMessage.Contains("Error") || responseMessage.Contains("error") || responseMessage.Contains("fail"))
                        {
                            responseType = "error";
                        }
                        else if (responseMessage.Contains("premium") || responseMessage.Contains("Premium"))
                        {
                            responseType = "warning";
                        }

                        await UpdateLogMessage(responseMessage, responseType);
                    }
                }
                catch
                {

                }

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof processServerResponse === 'function') {{
                                processServerResponse({jsonData});
                            }}

                            console.log('Server response processed successfully');
                        }} catch (error) {{
                            console.error('Error processing server response:', error);
                        }}
                    ");
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processed server response and updated user data{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing server response: {ex.Message}{Environment.NewLine}");
            }
        }

        private string GetDeviceId()
        {
            try
            {
                // Generate a stable hardware-based device ID (same logic as LoginForm)
                string hardwareId = GetHardwareId();

                // Create a deterministic device ID from hardware hash
                using (System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hardwareId));
                    // Convert first 8 bytes to hex string for a compact ID
                    string hashHex = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "");
                    return $"HW-{hashHex}";
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting device ID: {ex.Message}{Environment.NewLine}");

                // Fallback: use machine name + username hash
                string fallback = $"{Environment.MachineName}_{Environment.UserName}";
                using (System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(fallback));
                    string hashHex = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "");
                    return $"FB-{hashHex}";
                }
            }
        }

        private string GetHardwareId()
        {
            System.Text.StringBuilder hardwareInfo = new System.Text.StringBuilder();

            try
            {
                // Get CPU ID (most stable identifier)
                string cpuId = GetWmiValue("Win32_Processor", "ProcessorId");
                if (!string.IsNullOrEmpty(cpuId))
                {
                    hardwareInfo.Append(cpuId);
                }

                // Get Motherboard Serial Number
                string motherboardSerial = GetWmiValue("Win32_BaseBoard", "SerialNumber");
                if (!string.IsNullOrEmpty(motherboardSerial) && motherboardSerial != "None")
                {
                    hardwareInfo.Append(motherboardSerial);
                }

                // Get BIOS Serial Number
                string biosSerial = GetWmiValue("Win32_BIOS", "SerialNumber");
                if (!string.IsNullOrEmpty(biosSerial) && biosSerial != "None")
                {
                    hardwareInfo.Append(biosSerial);
                }

                // Get Windows Machine GUID (very stable, survives OS reinstall if same disk)
                string machineGuid = GetMachineGuid();
                if (!string.IsNullOrEmpty(machineGuid))
                {
                    hardwareInfo.Append(machineGuid);
                }

                // Fallback if no hardware info found
                if (hardwareInfo.Length == 0)
                {
                    hardwareInfo.Append($"{Environment.MachineName}_{Environment.UserName}_{Environment.OSVersion}");
                }

                return hardwareInfo.ToString();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error collecting hardware info: {ex.Message}{Environment.NewLine}");
                return $"{Environment.MachineName}_{Environment.UserName}_{Environment.ProcessorCount}";
            }
        }

        private string GetWmiValue(string wmiClass, string wmiProperty)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher($"SELECT {wmiProperty} FROM {wmiClass}"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        object value = obj[wmiProperty];
                        if (value != null)
                        {
                            string result = value.ToString().Trim();
                            // Filter out common placeholder values
                            if (!string.IsNullOrEmpty(result) &&
                                result != "None" &&
                                result != "To Be Filled By O.E.M." &&
                                result != "Default string" &&
                                result != "System Serial Number")
                            {
                                return result;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WMI query failed for {wmiClass}.{wmiProperty}: {ex.Message}{Environment.NewLine}");
            }
            return string.Empty;
        }

        private string GetMachineGuid()
        {
            try
            {
                // Windows Machine GUID from registry - very stable identifier
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("MachineGuid");
                        if (value != null)
                        {
                            return value.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to get MachineGuid: {ex.Message}{Environment.NewLine}");
            }
            return string.Empty;
        }

        private string GetCpuId()
        {
            try
            {

                string cpuInfo = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
                string procCount = Environment.ProcessorCount.ToString();
                return $"{cpuInfo}-{procCount}";
            }
            catch
            {
                return Environment.ProcessorCount.ToString();
            }
        }

        private string GetDiskId()
        {
            try
            {

                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                string volumeSerial = GetVolumeSerial(systemDrive);
                return volumeSerial;
            }
            catch
            {
                return Environment.SystemDirectory;
            }
        }

        private string GenerateUniqueId()
        {
            return Guid.NewGuid().ToString();
        }

        private async Task UpdateHeartbeatStatus(string status, string message = "")
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                string escapedMessage = message.Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");

                await webView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        if (typeof updateHeartbeatStatus === 'function') {{
                            updateHeartbeatStatus('{status}', '{escapedMessage}');
                        }} else {{
                            console.log('Heartbeat status: {status}');

                            const statusEl = document.getElementById('heartbeat-status');
                            if (statusEl) {{
                                statusEl.textContent = '{status}';
                                statusEl.className = 'status-{status}';
                            }}
                        }}
                    }} catch (error) {{
                        console.error('Error updating heartbeat status:', error);
                    }}
                ");
            }
        }

        private async Task<string> TestEndpointsFromCSharp()
        {
            try
            {
                var results = new List<object>();

                // Test Main Website - ANY response (even errors) means it's reachable
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        var websiteRequest = new HttpRequestMessage(HttpMethod.Head, "https://apiurl/");
                        websiteRequest.Headers.Add("User-Agent", "SWA-Launcher/2.0");

                        // Use HttpClient directly with CancellationToken for timeout
                        var websiteResponse = await HttpClientManager.Client.SendAsync(websiteRequest, cts.Token);

                        // If we got ANY response at all, even 404/500, it means the server is reachable
                        results.Add(new
                        {
                            name = "Main Website",
                            success = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    // ONLY mark as failed if it's a REAL network error (no response at all)
                    if (ex is TaskCanceledException || ex is TimeoutException || ex is OperationCanceledException)
                    {
                        // Timeout - server didn't respond
                        results.Add(new
                        {
                            name = "Main Website",
                            success = false,
                            errorType = "Timeout",
                            errorCode = "TIMEOUT"
                        });

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Main Website test failed (timeout): {ex.Message}{Environment.NewLine}");
                    }
                    else if (ex is HttpRequestException)
                    {
                        // Network error - can't reach server
                        results.Add(new
                        {
                            name = "Main Website",
                            success = false,
                            errorType = "Network Error",
                            errorCode = "NETWORK_ERROR"
                        });

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Main Website test failed (network): {ex.Message}{Environment.NewLine}");
                    }
                    else
                    {
                        // Some other error, but still mark as success (server exists)
                        results.Add(new
                        {
                            name = "Main Website",
                            success = true
                        });
                    }
                }

                // Test API Server - ANY response (even errors) means it's reachable
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        var apiRequest = new HttpRequestMessage(HttpMethod.Get, "https://apiurl/api/v3/");
                        apiRequest.Headers.Add("User-Agent", "SWA-Launcher/2.0");

                        var apiResponse = await HttpClientManager.Client.SendAsync(apiRequest, cts.Token);

                        // If we got ANY response at all (200, 404, 500, whatever), server is reachable
                        results.Add(new
                        {
                            name = "API Server",
                            success = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    // ONLY mark as failed if it's a REAL network error (no response at all)
                    if (ex is TaskCanceledException || ex is TimeoutException || ex is OperationCanceledException)
                    {
                        // Timeout - server didn't respond
                        results.Add(new
                        {
                            name = "API Server",
                            success = false,
                            errorType = "Timeout",
                            errorCode = "TIMEOUT"
                        });

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: API Server test failed (timeout): {ex.Message}{Environment.NewLine}");
                    }
                    else if (ex is HttpRequestException)
                    {
                        // Network error - can't reach server
                        results.Add(new
                        {
                            name = "API Server",
                            success = false,
                            errorType = "Network Error",
                            errorCode = "NETWORK_ERROR"
                        });

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: API Server test failed (network): {ex.Message}{Environment.NewLine}");
                    }
                    else
                    {
                        // Some other error, but still mark as success (server exists)
                        results.Add(new
                        {
                            name = "API Server",
                            success = true
                        });
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Endpoint tests completed: {JsonConvert.SerializeObject(results)}{Environment.NewLine}");

                return JsonConvert.SerializeObject(results);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in TestEndpointsFromCSharp: {ex.Message}{Environment.NewLine}");
                return "[]";
            }
        }

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson.Trim('"');

            File.AppendAllText(@"C:\GFK\dashboard_message_log.txt", $"{DateTime.Now}: Message received: {message}{Environment.NewLine}");

            if (message == "test-endpoints")
            {
                // Handle endpoint testing request from JavaScript
                Task.Run(async () =>
                {
                    try
                    {
                        string results = await TestEndpointsFromCSharp();

                        // Execute JavaScript on UI thread
                        this.Invoke((MethodInvoker)(async () =>
                        {
                            try
                            {
                                await webView.CoreWebView2.ExecuteScriptAsync($"handleEndpointTestResults({results});");
                            }
                            catch (Exception jsEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Error executing JavaScript: {jsEx.Message}{Environment.NewLine}");
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Error testing endpoints: {ex.Message}{Environment.NewLine}");
                    }
                });
            }
            else if (message.Contains("Server response:"))
            {

                await ProcessServerResponse(message);
            }
            else if (message == "minimize")
            {
                this.WindowState = FormWindowState.Minimized;
            }
            else if (message == "close")
            {
                this.Close();
            }
            else if (message == "drag:start")
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
            else if (message.StartsWith("resize:"))
            {
                try
                {

                    string dimensions = message.Substring("resize:".Length);
                    string[] parts = dimensions.ToLower().Split('x');

                    if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
                    {

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Resize request: {width}x{height}{Environment.NewLine}");

                        this.Size = new Size(width, height);

                        this.CenterToScreen();
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing resize message: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message.StartsWith("swa:"))
            {
                bool enableSwa = message.Substring("swa:".Length) == "enable";

                SetSwaStatus(enableSwa);
            }
            else if (message.StartsWith("toggleGame:"))
            {

                string[] parts = message.Split(':');
                if (parts.Length == 3)
                {
                    string gameId = parts[1];
                    bool isEnabled = bool.Parse(parts[2]);
                    ToggleGame(gameId, isEnabled);
                }
            }
            else if (message == "browseFile")
            {

                BrowseForFile();
            }
            else if (message.StartsWith("copyFile:"))
            {

                string destinationPath = message.Substring("copyFile:".Length);
                CopySelectedFileTo(destinationPath);
            }
            else if (message.StartsWith("heartbeat:"))
            {

                string action = message.Substring("heartbeat:".Length);

                if (action == "start")
                {
                    if (heartbeatSystem == null)
                    {
                        try
                        {

                            string userDataPath = @"C:\GFK\user_data.json";
                            if (File.Exists(userDataPath))
                            {
                                dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(userDataPath));
                                string username = userData?.username ?? "Guest";
                                string hwid = userData?.hwid ?? userData?.device_id ?? GetDeviceId();
                                string uniqueId = userData?.unique_id ?? $"guest_{hwid}";

                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Manual heartbeat with unique_id: {uniqueId}{Environment.NewLine}");

                                heartbeatSystem = new HeartbeatSystem(username, hwid, uniqueId, APP_VERSION);
                            }
                            else
                            {

                                string guestName = "Guest_" + DateTime.Now.Ticks.ToString().Substring(10);
                                string hwid = GetDeviceId();
                                string uniqueId = $"guest_{hwid}";

                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Manual guest heartbeat with unique_id: {uniqueId}{Environment.NewLine}");

                                heartbeatSystem = new HeartbeatSystem(guestName, hwid, uniqueId, APP_VERSION);
                            }

                            if (heartbeatSystem.IsEnabled)
                            {
                                heartbeatSystem.Start();
                                await UpdateHeartbeatStatus("active");
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system started manually{Environment.NewLine}");
                            }
                            else
                            {
                                await UpdateHeartbeatStatus("disabled", "Heartbeat is disabled in the configuration");
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Manual heartbeat start request ignored - disabled in config{Environment.NewLine}");
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error starting heartbeat system: {ex.Message}{Environment.NewLine}");
                            await UpdateHeartbeatStatus("error", ex.Message);
                        }
                    }
                    else
                    {

                        await UpdateHeartbeatStatus(heartbeatSystem.IsEnabled ? "active" : "disabled");
                    }
                }
                else if (action == "stop")
                {
                    if (heartbeatSystem != null)
                    {
                        heartbeatSystem.Stop();
                        heartbeatSystem = null;
                        await UpdateHeartbeatStatus("inactive");
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Heartbeat system stopped manually{Environment.NewLine}");
                    }
                }
                else if (action == "status")
                {

                    if (heartbeatSystem == null)
                    {
                        await UpdateHeartbeatStatus("inactive");
                    }
                    else
                    {
                        await UpdateHeartbeatStatus(heartbeatSystem.IsEnabled ? "active" : "disabled");
                    }
                }
                else if (action == "config")
                {

                    if (ApiConfigManager.Config?.Heartbeat != null)
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            var config = ApiConfigManager.Config.Heartbeat;
                            await webView.CoreWebView2.ExecuteScriptAsync($@"
                                try {{
                                    if (typeof updateHeartbeatConfig === 'function') {{
                                        updateHeartbeatConfig({{
                                            enabled: {config.Enabled.ToString().ToLower()},
                                            enabledForGuests: {config.EnabledForGuests.ToString().ToLower()},
                                            intervalMs: {config.IntervalMs}
                                        }});
                                    }}
                                }} catch (error) {{
                                    console.error('Error updating heartbeat config:', error);
                                }}
                            ");
                        }
                    }
                }
            }
            else if (message == "logout")
            {
                try
                {
                    // Delete connection.dat file
                    string connectionCodeFile = @"C:\GFK\connection.dat";
                    if (File.Exists(connectionCodeFile))
                    {
                        File.Delete(connectionCodeFile);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully deleted connection.dat file on logout{Environment.NewLine}");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error deleting connection.dat on logout: {ex.Message}{Environment.NewLine}");
                }

                this.DialogResult = DialogResult.OK;
                this.Close();

                LoginForm loginForm = new LoginForm();
                loginForm.Show();
            }
            else if (message == "getSteamPath")
            {

                GetSteamInstallPath();
            }
            else if (message == "checkPlugin")
            {

                GetSteamInstallPath();
            }
            else if (message == "scanGames")
            {

                GetSteamInstallPath();
            }
            else if (message == "getErrorLog")
            {

                SendErrorLogToUI();
            }
            else if (message == "clearErrorLog")
            {

                ClearErrorLog();
            }

            else if (message == "checkForUpdates" || message.StartsWith("checkForUpdates:"))
            {
                bool showNotification = message.Contains(":") && message.Split(':')[1] == "true";

                CheckForUpdates(showNotification).ConfigureAwait(false);
            }
            else if (message == "downloadUpdate" || message.StartsWith("downloadUpdate:"))
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Download update requested via UI{Environment.NewLine}");

                // Start download in background
                Task.Run(async () => await DownloadAndInstallUpdateAsync());
            }
            else if (message == "getPatchNotes")
            {

                GetPatchNotesAsync().ConfigureAwait(false);
            }
            else if (message == "getBannerData")
            {
                GetBannerDataAsync().ConfigureAwait(false);
            }
            else if (message == "open-support")
            {
                // Open support URL in default browser
                try
                {
                    System.Diagnostics.Process.Start("https://apiurl/support");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error opening support URL: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message == "open-premium")
            {
                // Open premium page URL in default browser
                try
                {
                    System.Diagnostics.Process.Start("https://apiurl/premium");
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Opened premium page URL{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error opening premium URL: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message == "getCurrentVersion")
            {

                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof updateVersionDisplay === 'function') {{
                                updateVersionDisplay('{APP_VERSION}');
                            }}
                        }} catch (error) {{
                            console.error('Error updating version display:', error);
                        }}
                    ");
                }
            }
            else if (message == "get-device-id")
            {
                // Send device ID to JavaScript
                string deviceId = GetDeviceId();
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            console.log('Received device ID from C#: {deviceId}');
                            if (typeof userAccountInfo !== 'undefined') {{
                                userAccountInfo.device_id = '{deviceId}';
                                userAccountInfo.hwid = '{deviceId}';
                            }}
                            const deviceIdElement = document.getElementById('device-id-value');
                            if (deviceIdElement) {{
                                deviceIdElement.textContent = '{deviceId}';
                            }}
                        }} catch (error) {{
                            console.error('Error updating device ID:', error);
                        }}
                    ");
                }
            }
            else if (message.StartsWith("autoUpdate:"))
            {

                bool enableAutoUpdate = message.Substring("autoUpdate:".Length) == "true";
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Auto-update preference set to: {(enableAutoUpdate ? "enabled" : "disabled")}{Environment.NewLine}");
            }
            else if (message.StartsWith("test:"))
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Test message received: {message}{Environment.NewLine}");
            }
            else if (message.StartsWith("addGame:"))
            {
                string messageContent = message.Substring("addGame:".Length);
                string[] parts = messageContent.Split(':');
                string gameId = parts[0];
                bool includeDlc = parts.Length > 1 && bool.Parse(parts[1]);
                TryAddGameAsync(gameId, false, includeDlc);
            }
            else if (message.StartsWith("forceAddGame:"))
            {
                string messageContent = message.Substring("forceAddGame:".Length);
                string[] parts = messageContent.Split(':');
                string gameId = parts[0];
                bool includeDlc = parts.Length > 1 && bool.Parse(parts[1]);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Force adding game {gameId} (bypassing warnings){Environment.NewLine}");
                TryAddGameAsync(gameId, false, includeDlc, true);
            }
            else if (message == "open-notice-faq")
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://apiurl/noticefaq",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening notice FAQ: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message.StartsWith("open-url:"))
            {
                try
                {
                    string url = message.Substring("open-url:".Length);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening URL: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message == "selectSteamPath")
            {
                SelectCustomSteamPath();
            }
            else if (message.StartsWith("savePreferredExecutable:"))
            {
                string executable = message.Substring("savePreferredExecutable:".Length);
                SavePreferredExecutable(executable);
            }
            else if (message == "loadSteamSettings")
            {
                LoadSteamSettings();
            }
            else if (message == "repairPlugin")
            {
                RepairPlugin();
            }
            else if (message.StartsWith("saveSteamPathManual:"))
            {
                string path = message.Substring("saveSteamPathManual:".Length);
                SaveSteamPathManual(path);
            }
            else if (message == "openSteamConfigFile")
            {
                OpenSteamConfigFile();
            }
            else if (message == "get-debug-mode")
            {
                // Send debug mode status to frontend
                bool debugMode = IsDebugMode();
                string jsonResponse = $"{{\"type\":\"debug-mode\",\"enabled\":{debugMode.ToString().ToLower()}}}";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending debug mode status: {jsonResponse}{Environment.NewLine}");
                webView.CoreWebView2.PostWebMessageAsString(jsonResponse);
            }
            else if (message == "open-devtools")
            {
                // Only allow if debug mode is enabled
                if (IsDebugMode())
                {
                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Opening DevTools window{Environment.NewLine}");
                            webView.CoreWebView2.OpenDevToolsWindow();
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot open DevTools - WebView not initialized{Environment.NewLine}");
                        }
                    }
                    catch (Exception devToolsEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening DevTools: {devToolsEx.Message}{Environment.NewLine}");
                    }
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DevTools blocked - debug mode not enabled{Environment.NewLine}");
                }
            }
            else if (message.StartsWith("updateGame:"))
            {

                string gameId = message.Substring("updateGame:".Length);
                TryAddGameAsync(gameId, true, false);
            }
            else if (message.StartsWith("forceUpdateGame:"))
            {
                string gameId = message.Substring("forceUpdateGame:".Length);
                TryAddGameAsync(gameId, true, false, true); // bypassWarnings = true
            }
            else if (message.StartsWith("game:remove:file:"))
            {
                string gameId = message.Substring("game:remove:file:".Length);
                RemoveGameFile(gameId);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Received remove file command for game {gameId}{Environment.NewLine}");
            }
            else if (message.StartsWith("game:remove:"))
            {
                string gameId = message.Substring("game:remove:".Length);
                RemoveGameAndManifest(gameId);
            }
            else if (message.StartsWith("removeDLC:"))
            {
                string dlcId = message.Substring("removeDLC:".Length);
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing DLC {dlcId} from SteamTools.lua{Environment.NewLine}");
                    bool success = RemoveDLCFromSteamTools(dlcId);
                    
                    if (success)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed DLC {dlcId} from SteamTools.lua{Environment.NewLine}");
                        
                        // Send success message to frontend
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            console.log('DLC {dlcId} successfully removed from SteamTools.lua');
                        ");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to remove DLC {dlcId} from SteamTools.lua{Environment.NewLine}");
                        
                        // Send error message to frontend
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            alert('Failed to remove DLC {dlcId} from SteamTools.lua');
                        ");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception while removing DLC {dlcId}: {ex.Message}{Environment.NewLine}");
                    
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        alert('Error removing DLC {dlcId}: {ex.Message.Replace("'", "\\'")}');
                    ");
                }
            }
            else if (message == "getInstalledDLCs")
            {
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Getting installed DLCs from SteamTools.lua{Environment.NewLine}");
                    string dlcData = await GetInstalledDLCsWithNamesAsync();

                    if (!string.IsNullOrEmpty(dlcData))
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                const data = {dlcData};
                                processDLCData(data);
                            }} catch (e) {{
                                console.error('Error processing DLC data:', e);
                                document.getElementById('dlc-cards-container').innerHTML = '<div class=""empty-state""><i class=""fas fa-exclamation-triangle""></i><p>Error processing DLC data</p></div>';
                            }}
                        ");
                    }
                    else
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            document.getElementById('dlc-cards-container').innerHTML = '<div class=""empty-state""><i class=""fas fa-puzzle-piece""></i><p>No DLC data found</p></div>';
                        ");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting installed DLCs: {ex.Message}{Environment.NewLine}");
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        document.getElementById('dlc-cards-container').innerHTML = '<div class=""empty-state""><i class=""fas fa-exclamation-triangle""></i><p>Error: {ex.Message.Replace("\"", "\\\"")}</p></div>';
                    ");
                }
            }
            else if (message == "dlcs:removeAll")
            {
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing all DLCs from Steamtools.lua{Environment.NewLine}");
                    bool success = RemoveAllDLCsFromSteamTools();
                    
                    if (success)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed all DLCs from Steamtools.lua{Environment.NewLine}");
                        
                        // Refresh DLC manager to show empty state
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            if (typeof loadDLCsFromAPI === 'function') {
                                loadDLCsFromAPI();
                                console.log('DLC manager refreshed after removing all DLCs');
                            }
                        ");
                        
                        // Show success message
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            updateLogMessage('All DLCs removed successfully!', 'success');
                        ");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to remove all DLCs from Steamtools.lua{Environment.NewLine}");
                        
                        // Show error message
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            updateLogMessage('Failed to remove all DLCs', 'error');
                        ");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception while removing all DLCs: {ex.Message}{Environment.NewLine}");
                    
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        updateLogMessage('Error removing DLCs: {ex.Message.Replace("'", "\\'")}', 'error');
                    ");
                }
            }
            else if (message.StartsWith("fetchDLC:"))
            {
                string gameId = message.Substring("fetchDLC:".Length);
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching DLC data for game {gameId}{Environment.NewLine}");
                    string dlcData = await FetchDLCDataAsync(gameId);

                    if (!string.IsNullOrEmpty(dlcData))
                    {

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                if (typeof processDLCData === 'function') {{
                                    processDLCData({dlcData});
                                    console.log('DLC data processed successfully for game {gameId}');
                                }} else {{
                                    console.error('processDLCData function not found');
                                }}
                            }} catch (error) {{
                                console.error('Error processing DLC data:', error);
                            }}
                        ");
                    }
                    else
                    {

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                if (typeof processDLCError === 'function') {{
                                    processDLCError('Failed to fetch DLC data');
                                }} else {{
                                    console.error('Failed to fetch DLC data for game {gameId}');
                                }}
                            }} catch (error) {{
                                console.error('Error handling DLC error:', error);
                            }}
                        ");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in DLC fetch handler: {ex.Message}{Environment.NewLine}");
                }
            }
            else if (message == "restartSteam")
            {
                try
                {
                    // Kill Steam process
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM steam.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    System.Threading.Thread.Sleep(2000);

                    // Get Steam path (custom or detected)
                    var settings = LoadSteamSettingsFromFile();
                    string steamPath = GetCurrentSteamPath();

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restart Steam - Using Steam path: {steamPath}\n");

                    if (steamPath != "Not found" && !string.IsNullOrEmpty(steamPath))
                    {
                        // Get preferred executable
                        string preferredExe = "swav2.exe"; // default
                        if (settings.ContainsKey("preferredExecutable") && !string.IsNullOrEmpty(settings["preferredExecutable"]))
                        {
                            preferredExe = settings["preferredExecutable"];
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restart Steam - Preferred executable: {preferredExe}\n");

                        string exePath = System.IO.Path.Combine(steamPath, preferredExe);

                        if (File.Exists(exePath))
                        {
                            System.Diagnostics.Process.Start(exePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam restarted successfully with {preferredExe} from {steamPath}\n");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ERROR: {preferredExe} not found at {exePath}\n");
                        }
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ERROR: Steam path not found, cannot restart.\n");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error restarting Steam: {ex.Message}\n");
                }
            }
            else if (message == "restartSteamAndApp")
            {
                try
                {
                    // Kill Steam process
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM steam.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    // Kill SWAV2 process
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM SWAV2.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    System.Threading.Thread.Sleep(2000);

                    // Get settings for preferred executable
                    var settings = LoadSteamSettingsFromFile();
                    string preferredExe = "swav2.exe"; // default
                    if (settings.ContainsKey("preferredExecutable") && !string.IsNullOrEmpty(settings["preferredExecutable"]))
                    {
                        preferredExe = settings["preferredExecutable"];
                    }

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: RestartSteamAndApp - Preferred executable: {preferredExe}\n");

                    // First try to restart the app from app directory
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string appExe = Path.Combine(appDir, "SWAV2.EXE");
                    bool appStarted = false;
                    if (File.Exists(appExe))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = appExe,
                            WorkingDirectory = appDir,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWAV2.EXE restarted from app folder: {appExe}\n");
                        appStarted = true;
                    }

                    // Now start Steam with preferred executable
                    string steamPath = GetCurrentSteamPath();

                    bool steamStarted = false;
                    if (steamPath != "Not found" && !string.IsNullOrEmpty(steamPath))
                    {
                        string steamExePath = Path.Combine(steamPath, preferredExe);
                        if (File.Exists(steamExePath))
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = steamExePath,
                                WorkingDirectory = steamPath,
                                UseShellExecute = true
                            };
                            System.Diagnostics.Process.Start(psi);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam restarted with {preferredExe} from {steamPath}\n");
                            steamStarted = true;
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: {preferredExe} not found at {steamExePath}\n");
                        }
                    }

                    if (!appStarted && !steamStarted)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Neither app nor Steam executable could be started.\n");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in restartSteamAndApp: {ex.Message}\n{ex.StackTrace}\n");
                }
            }
            else if (message == "patchSteam")
            {
                PatchSteamAsync().ConfigureAwait(false);
            }
            else if (message.StartsWith("fetchGameInfo:"))
            {
                string gameId = message.Substring("fetchGameInfo:".Length);
                FetchGameInfoAsync(gameId).ConfigureAwait(false);
            }
            else if (message == "removeAllGamesConfirmed")
            {
                RemoveAllGamesFromSteam();
            }
            else if (message == "dlcs:removeAll")
            {
                RemoveAllDLCs();
            }
            else if (message.StartsWith("dlcs:removeAllForGame:"))
            {
                string gameId = message.Substring("dlcs:removeAllForGame:".Length);
                if (gameId == "undefined" || string.IsNullOrEmpty(gameId))
                {
                    // If gameId is undefined, treat as remove all DLCs
                    RemoveAllDLCs();
                }
                else
                {
                    // Remove DLCs for specific game ONLY
                    try
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing all DLCs for game {gameId}{Environment.NewLine}");

                        bool success = await RemoveGameDLCsFromSteamToolsAsync(gameId);

                        if (success)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed all DLCs for game {gameId}{Environment.NewLine}");

                            // Refresh DLC manager to show updated state
                            await webView.CoreWebView2.ExecuteScriptAsync(@"
                                if (typeof loadDLCsFromAPI === 'function') {
                                    loadDLCsFromAPI();
                                    console.log('DLC manager refreshed after removing game DLCs');
                                }
                            ");

                            await webView.CoreWebView2.ExecuteScriptAsync(@"
                                updateLogMessage('All DLCs for game removed successfully!', 'success');
                            ");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to remove DLCs for game {gameId}{Environment.NewLine}");

                            await webView.CoreWebView2.ExecuteScriptAsync(@"
                                updateLogMessage('Failed to remove game DLCs', 'error');
                            ");
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception removing DLCs for game {gameId}: {ex.Message}{Environment.NewLine}");

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            updateLogMessage('Error removing game DLCs: {ex.Message.Replace("'", "\\'")}', 'error');
                        ");
                    }
                }
            }
        }
        private void SetSwaStatus(bool enabled)
        {
            try
            {

                string registryPath = @"Software\Valve\Steamtools";

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
                {
                    if (key != null)
                    {
                        key.SetValue("ActivateUnlockMode", enabled ? "true" : "false");
                        key.SetValue("AlwaysStayUnlocked", enabled ? "true" : "false");
                        key.SetValue("FloatingVisible", "false");
                        key.SetValue("notUnlockDepot", "false");
                    }
                }

                Console.WriteLine($"SWA has been {(enabled ? "enabled" : "disabled")}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA has been {(enabled ? "enabled" : "disabled")}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Registry values updated - ActivateUnlockMode: {enabled}, AlwaysStayUnlocked: {enabled}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error setting SWA status: {ex.Message}{Environment.NewLine}");
            }
        }

        private void RemoveAllGamesFromSteam()
        {
            try
            {
                // Get Steam path from registry
                string steamPath = GetSteamPathFromRegistry();
                
                if (steamPath == "Not found")
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found in registry{Environment.NewLine}");
                    return;
                }

                // Construct path to config/stplug-in folder
                string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                
                if (!Directory.Exists(pluginPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin directory not found: {pluginPath}{Environment.NewLine}");
                    return;
                }

                // Find all .lua files in the plugin directory
                string[] luaFiles = Directory.GetFiles(pluginPath, "*.lua");
                
                int deletedCount = 0;
                foreach (string luaFile in luaFiles)
                {
                    try
                    {
                        File.Delete(luaFile);
                        deletedCount++;
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted game file: {Path.GetFileName(luaFile)}{Environment.NewLine}");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error deleting file {luaFile}: {ex.Message}{Environment.NewLine}");
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully deleted {deletedCount} game files from Steam plugin directory{Environment.NewLine}");
                
                // Notify the web interface about the operation result
                webView.CoreWebView2.ExecuteScriptAsync($"showNotification('Successfully removed {deletedCount} games from Steam library', 'success');");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in RemoveAllGamesFromSteam: {ex.Message}{Environment.NewLine}");
                webView.CoreWebView2.ExecuteScriptAsync($"showNotification('Error removing games: {ex.Message.Replace("'", "\\'")}', 'error');");
            }
        }

        private void RemoveAllDLCs()
        {
            try
            {
                // Get Steam path from registry
                string steamPath = GetSteamPathFromRegistry();
                
                if (steamPath == "Not found")
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found in registry{Environment.NewLine}");
                    return;
                }

                // Construct path to config/stplug-in folder and Steamtools.lua file
                string pluginPath = Path.Combine(steamPath, "config", "stplug-in");
                string steamtoolsPath = Path.Combine(pluginPath, "Steamtools.lua");
                
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Looking for Steamtools.lua at: {steamtoolsPath}{Environment.NewLine}");
                
                // Check if the plugin directory exists
                if (!Directory.Exists(pluginPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin directory not found: {pluginPath}{Environment.NewLine}");
                    webView.CoreWebView2.ExecuteScriptAsync("showNotification('Plugin directory not found', 'error');");
                    return;
                }
                
                try
                {
                    // Delete the file if it exists
                    if (File.Exists(steamtoolsPath))
                    {
                        File.Delete(steamtoolsPath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully deleted Steamtools.lua file{Environment.NewLine}");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steamtools.lua file not found, creating new one{Environment.NewLine}");
                    }
                    
                    // Create a new empty file with the same name
                    File.WriteAllText(steamtoolsPath, string.Empty);
                    
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully created new empty Steamtools.lua file{Environment.NewLine}");
                    
                    // Notify the web interface about the operation result
                    webView.CoreWebView2.ExecuteScriptAsync("showNotification('DLC removed', 'success');");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error recreating Steamtools.lua: {ex.Message}{Environment.NewLine}");
                    webView.CoreWebView2.ExecuteScriptAsync($"showNotification('Error recreating Steamtools.lua: {ex.Message.Replace("'", "\\'")}', 'error');");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in RemoveAllDLCs: {ex.Message}{Environment.NewLine}");
                webView.CoreWebView2.ExecuteScriptAsync($"showNotification('Error removing DLCs: {ex.Message.Replace("'", "\\'")}', 'error');");
            }
        }

        private async void InitializeSwaToggleState()
        {
            try
            {
                string registryPath = @"Software\Valve\Steamtools";
                bool isSwaEnabled = false;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {

                        object activateUnlockModeValue = key.GetValue("ActivateUnlockMode");
                        object alwaysStayUnlockedValue = key.GetValue("AlwaysStayUnlocked");

                        bool activateUnlockMode = activateUnlockModeValue != null &&
                             (activateUnlockModeValue.ToString().ToLower() == "true" || activateUnlockModeValue.ToString() == "1");

                        bool alwaysStayUnlocked = alwaysStayUnlockedValue != null &&
                             (alwaysStayUnlockedValue.ToString().ToLower() == "true" || alwaysStayUnlockedValue.ToString() == "1");
                        isSwaEnabled = activateUnlockMode || alwaysStayUnlocked;

                        using (RegistryKey writeKey = Registry.CurrentUser.CreateSubKey(registryPath))
                        {
                            if (writeKey != null)
                            {
                                writeKey.SetValue("FloatingVisible", "false");
                                writeKey.SetValue("notUnlockDepot", "false");
                            }
                        }
                    }
                    else
                    {

                        using (RegistryKey createKey = Registry.CurrentUser.CreateSubKey(registryPath))
                        {
                            if (createKey != null)
                            {
                                createKey.SetValue("ActivateUnlockMode", "false");
                                createKey.SetValue("AlwaysStayUnlocked", "false");
                                createKey.SetValue("FloatingVisible", "false");
                                createKey.SetValue("notUnlockDepot", "false");
                            }
                        }
                    }
                }

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                                        try {{
                                            if (typeof toggleSwa === 'function') {{
                                                const swaToggle = document.getElementById('enable-swa');
                                                if (swaToggle) {{
                                                    swaToggle.checked = {isSwaEnabled.ToString().ToLower()};
                                                    toggleSwa({isSwaEnabled.ToString().ToLower()});
                                                }}
                                            }}
                                            console.log('SWA toggle state initialized to: {isSwaEnabled}');
                                        }} catch (error) {{
                                            console.error('Error initializing SWA toggle state:', error);
                                        }}
                                    ");
                }
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA toggle state initialized. Enabled: {isSwaEnabled}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error initializing SWA toggle state: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void GetSteamInstallPath()
        {
            try
            {

                if (Interlocked.Increment(ref steamPathCallCount) > 2)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Too many GetSteamInstallPath calls, skipping to prevent stack overflow{Environment.NewLine}");
                    Interlocked.Decrement(ref steamPathCallCount);
                    return;
                }

                // Use GetCurrentSteamPath which handles custom path priority
                string steamPath = GetCurrentSteamPath();

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using Steam path: {steamPath}{Environment.NewLine}");

                CheckSteamPlugin(steamPath);

                if (webView != null && webView.CoreWebView2 != null)
                {

                    string escapedPath = steamPath.Replace("\\", "\\\\");

                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof updateSteamPath === 'function') {{
                                updateSteamPath('{escapedPath}');
                            }}
                            console.log('Steam path sent to UI: {escapedPath}');
                        }} catch (error) {{
                            console.error('Error updating Steam path:', error);
                        }}
                    ");
                }

                Interlocked.Decrement(ref steamPathCallCount);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error detecting Steam path: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void CheckSteamPlugin(string steamPath)
        {
            try
            {
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    await UpdatePluginStatus("Unknown (Steam path not found)");
                    return;
                }

                string hidDllPath = Path.Combine(steamPath, "hid.dll");
                bool hidDllExists = File.Exists(hidDllPath);

                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                bool pluginFolderExists = Directory.Exists(pluginFolderPath);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin detection - hid.dll: {hidDllExists}, plugin folder: {pluginFolderExists}{Environment.NewLine}");

                string statusMessage;
                if (hidDllExists && pluginFolderExists)
                {
                    statusMessage = "Detected";

                    if (pluginFolderExists)
                    {
                        ScanPluginGames(pluginFolderPath);
                    }
                }
                else if (hidDllExists || pluginFolderExists)
                {
                    statusMessage = "Partially";

                    if (pluginFolderExists)
                    {
                        ScanPluginGames(pluginFolderPath);
                    }

                    // Auto-install missing components
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin partially detected, attempting auto-install{Environment.NewLine}");
                    AutoInstallPluginComponents(steamPath, hidDllExists, pluginFolderExists);
                }
                else
                {
                    statusMessage = "Not installed";

                    // Auto-install all components
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin not detected, attempting auto-install{Environment.NewLine}");
                    AutoInstallPluginComponents(steamPath, hidDllExists, pluginFolderExists);
                }

                await UpdatePluginStatus(statusMessage);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking plugin: {ex.Message}{Environment.NewLine}");
                await UpdatePluginStatus("Error checking plugin");
            }
        }

        private async void ScanPluginGames(string pluginFolderPath)
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Starting to scan for plugin games in {pluginFolderPath}{Environment.NewLine}");

                string[] luaFiles = Directory.GetFiles(pluginFolderPath, "*.lua");
                string[] ludaFiles = Directory.GetFiles(pluginFolderPath, "*.luda");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Raw .lua files: {string.Join(", ", luaFiles)}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Raw .luda files: {string.Join(", ", ludaFiles)}{Environment.NewLine}");

                Dictionary<string, string> allGameFiles = new Dictionary<string, string>();

                HashSet<string> allIds = new HashSet<string>();
                foreach (string file in luaFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.All(char.IsDigit))
                        allIds.Add(fileName);
                }
                foreach (string file in ludaFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.All(char.IsDigit))
                        allIds.Add(fileName);
                }

                Dictionary<string, string> lastUpdates = new Dictionary<string, string>();
                string lastUpdatePath = @"C:\GFK\last_update.json";
                if (File.Exists(lastUpdatePath))
                {
                    var json = File.ReadAllText(lastUpdatePath);
                    lastUpdates = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                                  ?? new Dictionary<string, string>();
                }

                foreach (string gameId in allIds)
                {
                    bool hasLua = File.Exists(Path.Combine(pluginFolderPath, gameId + ".lua"));
                    bool hasluda = File.Exists(Path.Combine(pluginFolderPath, gameId + ".luda"));
                    string fileType = hasLua ? "lua" : (hasluda ? "luda" : "none");
                    bool isEnabled = hasLua;
                    string lastUpdate = lastUpdates.ContainsKey(gameId) ? lastUpdates[gameId] : "";
                    allGameFiles[gameId] = fileType;
                }

                var gameFiles = new List<Dictionary<string, object>>();
                var debugList = new List<string>();

                foreach (var entry in allGameFiles)
                {
                    string gameId = entry.Key;
                    string fileType = entry.Value;
                    bool isEnabled = fileType == "lua";
                    string lastUpdate = lastUpdates.ContainsKey(gameId) ? lastUpdates[gameId] : "";

                    gameFiles.Add(new Dictionary<string, object> {
                        { "id", gameId },
                        { "enabled", isEnabled },
                        { "fileType", fileType },
                        { "last_update", lastUpdate }
                    });

                    debugList.Add($"{gameId}.{fileType}");
                }

                gameFiles = gameFiles.OrderBy(g => int.Parse(g["id"].ToString())).ToList();

                foreach (var game in gameFiles)
                {
                    var lastUpdate = game.ContainsKey("last_update") ? game["last_update"] : "";
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Game ID: {game["id"]}, Enabled: {game["enabled"]}, FileType: {game["fileType"]}, Last Update: {lastUpdate}{Environment.NewLine}");
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Total unique game files found: {gameFiles.Count}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All .lua/.luda files: {string.Join(", ", debugList)}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {

                    var settings = new Newtonsoft.Json.JsonSerializerSettings
                    {
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None,
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
                    };

                    string gamesJson = Newtonsoft.Json.JsonConvert.SerializeObject(gameFiles,
                        Newtonsoft.Json.Formatting.None, settings);

                    string debugListJson = Newtonsoft.Json.JsonConvert.SerializeObject(debugList);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending games JSON to UI: {gamesJson}{Environment.NewLine}");

                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                try {{
                    console.clear(); 
                    console.log('Received fresh game data from backend');

                    if (typeof updateGamesList === 'function') {{
                        updateGamesList({gamesJson});
                    }}
                    if (typeof updateDebugGamesList === 'function') {{
                        updateDebugGamesList({debugListJson});
                    }}
                    console.log('Game list sent to UI: {gameFiles.Count} games');

                    if (typeof renderGamesPage === 'function') {{
                        renderGamesPage();
                    }}
                }} catch (error) {{
                    console.error('Error updating games list:', error);
                }}
            ");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WebView not available to send games list{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error scanning plugin games: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        console.error('Error scanning games: {ex.Message.Replace("'", "\\'")}');
                    ");
                }
            }
        }

        private void ToggleGame(string gameId, bool enable)
        {
            try
            {
                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ToggleGame using Steam path: {steamPath}{Environment.NewLine}");

                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot toggle game, Steam path not found{Environment.NewLine}");
                    return;
                }

                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                if (!Directory.Exists(pluginFolderPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot toggle game, plugin folder not found{Environment.NewLine}");
                    return;
                }

                string cleanGameId = gameId.Replace(".lua", "").Replace(".luda", "");

                string luaFileName = $"{cleanGameId}.lua";
                string ludaFileName = $"{cleanGameId}.luda";
                string luaFilePath = Path.Combine(pluginFolderPath, luaFileName);
                string ludaFilePath = Path.Combine(pluginFolderPath, ludaFileName);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Toggle requested for game {cleanGameId}. Checking files: lua={File.Exists(luaFilePath)}, luda={File.Exists(ludaFilePath)}, enable={enable}{Environment.NewLine}");

                bool success = ExecuteWithElevation(() =>
                {
                    if (enable && File.Exists(ludaFilePath))
                    {

                        if (File.Exists(luaFilePath))
                        {

                            File.Delete(luaFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted existing {luaFilePath}{Environment.NewLine}");
                        }

                        File.Move(ludaFilePath, luaFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} enabled (renamed from .luda to .lua){Environment.NewLine}");
                    }
                    else if (!enable && File.Exists(luaFilePath))
                    {

                        if (File.Exists(ludaFilePath))
                        {

                            File.Delete(ludaFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted existing {ludaFilePath}{Environment.NewLine}");
                        }

                        File.Move(luaFilePath, ludaFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} disabled (renamed from .lua to .luda){Environment.NewLine}");
                    }
                    else if (enable && !File.Exists(ludaFilePath) && !File.Exists(luaFilePath))
                    {

                        File.WriteAllText(luaFilePath, string.Empty);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} created as enabled (.lua){Environment.NewLine}");
                    }
                    else if (!enable && !File.Exists(ludaFilePath) && !File.Exists(luaFilePath))
                    {

                        File.WriteAllText(ludaFilePath, string.Empty);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} created as disabled (.luda){Environment.NewLine}");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Could not toggle game {cleanGameId}. Unexpected file state: lua={File.Exists(luaFilePath)}, luda={File.Exists(ludaFilePath)}, enable={enable}{Environment.NewLine}");
                    }
                }, $"toggle game {cleanGameId}");

                if (!success)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to toggle game {cleanGameId}{Environment.NewLine}");
                    return;
                }

                System.Threading.Thread.Sleep(300);

                ScanPluginGames(pluginFolderPath);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error toggling game {gameId}: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task UpdatePluginStatus(string status)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {

                string escapedStatus = status.Replace("'", "\\'");

                await webView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        if (typeof updatePluginStatus === 'function') {{
                            updatePluginStatus('{escapedStatus}');
                        }}
                        console.log('Plugin status sent to UI: {escapedStatus}');
                    }} catch (error) {{
                        console.error('Error updating plugin status:', error);
                    }}
                ");
            }
        }

        private async Task UpdateLogMessage(string message, string type = "")
        {
            if (webView != null && webView.CoreWebView2 != null)
            {

                string escapedMessage = message.Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");

                await webView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        if (typeof updateLogMessage === 'function') {{
                            updateLogMessage('{escapedMessage}', '{type}');
                        }}
                        console.log('Log message updated: {escapedMessage}');
                    }} catch (error) {{
                        console.error('Error updating log message:', error);
                    }}
                ");
            }
        }

        private async Task StartRateLimitCountdown(int waitTimeSeconds)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof startRateLimitCountdown === 'function') {{
                                startRateLimitCountdown({waitTimeSeconds});
                            }} else {{

                                var remainingTime = {waitTimeSeconds};
                                var updateCountdown = function() {{
                                    if (remainingTime > 0) {{
                                        if (typeof updateLogMessage === 'function') {{
                                            updateLogMessage('Please wait ' + remainingTime + ' seconds before adding another game', 'warning');
                                        }}
                                        remainingTime--;
                                        setTimeout(updateCountdown, 1000);
                                    }} else {{
                                        if (typeof updateLogMessage === 'function') {{
                                            updateLogMessage('You can now add another game', 'success');
                                        }}
                                    }}
                                }};
                                updateCountdown();
                            }}
                        }} catch (error) {{
                            console.error('Error starting rate limit countdown:', error);
                        }}
                    ");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error starting rate limit countdown: {ex.Message}{Environment.NewLine}");
                }
            }
        }

        private async void BrowseForFile()
        {
            try
            {

                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "Select file to copy";
                    openFileDialog.Filter = "All files (*.*)|*.*";
                    openFileDialog.CheckFileExists = true;
                    openFileDialog.Multiselect = false;

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        selectedFilePath = openFileDialog.FileName;
                        string fileName = Path.GetFileName(selectedFilePath);

                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync($@"
                                try {{
                                    if (typeof updateSelectedFile === 'function') {{
                                        updateSelectedFile('{fileName}', '{selectedFilePath.Replace("\\", "\\\\")}');
                                    }}
                                    console.log('Selected file sent to UI: {fileName}');
                                }} catch (error) {{
                                    console.error('Error updating selected file:', error);
                                }}
                            ");
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: File selected: {selectedFilePath}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error browsing for file: {ex.Message}{Environment.NewLine}");
                await SendErrorMessageToUI("Error selecting file: " + ex.Message);
            }
        }

        private async void CopySelectedFileTo(string destinationFolder)
        {
            try
            {

                if (string.IsNullOrEmpty(selectedFilePath))
                {
                    await SendErrorMessageToUI("No file selected. Please browse and select a file first.");
                    return;
                }

                if (!File.Exists(selectedFilePath))
                {
                    await SendErrorMessageToUI("Selected file no longer exists.");
                    return;
                }

                string steamPath = GetSteamPathFromRegistry();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    await SendErrorMessageToUI("Steam folder not found. Please ensure Steam is installed.");
                    return;
                }

                string destinationDir = Path.Combine(steamPath, destinationFolder.TrimStart('\\'));

                if (!Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Created directory: {destinationDir}{Environment.NewLine}");
                }

                string fileName = Path.GetFileName(selectedFilePath);
                string destinationPath = Path.Combine(destinationDir, fileName);

                File.Copy(selectedFilePath, destinationPath, true);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Copied file: {selectedFilePath} to {destinationPath}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof fileCopyComplete === 'function') {{
                                fileCopyComplete(true, 'File successfully copied to Steam folder.', '{destinationPath.Replace("\\", "\\\\")}');
                            }}
                        }} catch (error) {{
                            console.error('Error updating UI after file copy:', error);
                        }}
                    ");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error copying file: {ex.Message}{Environment.NewLine}");
                await SendErrorMessageToUI("Error copying file: " + ex.Message);
            }
        }

        private string GetSteamPathFromRegistry()
        {
            string steamPath = "Not found";

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null)
                {
                    object path = key.GetValue("SteamPath");
                    if (path != null)
                    {
                        steamPath = path.ToString();
                    }
                }
            }

            return steamPath;
        }

        private async Task SendErrorMessageToUI(string errorMessage)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                string escapedError = errorMessage.Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");

                await webView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        if (typeof fileCopyComplete === 'function') {{
                            fileCopyComplete(false, '{escapedError}', '');
                        }}
                    }} catch (error) {{
                        console.error('Error sending error message to UI:', error);
                    }}
                ");
            }
        }

        private async void SendErrorLogToUI()
        {
            try
            {
                string errorLogPath = @"C:\GFK\errorlog.txt";
                string errorLogContent = "No error log found.";

                if (File.Exists(errorLogPath))
                {

                    var lines = new List<string>();
                    using (var fs = new FileStream(errorLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lines.Add(line);

                            if (lines.Count > 300)
                                lines.RemoveAt(0);
                        }
                    }

                    var formattedLines = new List<string>();
                    foreach (var line in lines)
                    {
                        string formattedLine = line;

                        if (line.Contains(DateTime.Now.Year.ToString()) && line.Contains(':'))
                        {
                            int colonIndex = line.IndexOf(':');
                            if (colonIndex > 0 && colonIndex < 30)
                            {

                                string timestamp = line.Substring(0, colonIndex + 3);
                                string restOfLine = line.Substring(colonIndex + 3);

                                formattedLine = $"<span class='timestamp'>{timestamp}</span>{restOfLine}";
                            }
                        }

                        if (line.Contains("Error") || line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            formattedLine = $"<span class='error'>{formattedLine}</span>";
                        }
                        else if (line.Contains("Warning") || line.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            formattedLine = $"<span class='warning'>{formattedLine}</span>";
                        }
                        else if (line.Contains("Success") || line.IndexOf("Detected", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            formattedLine = $"<span class='success'>{formattedLine}</span>";
                        }

                        formattedLines.Add(formattedLine);
                    }

                    errorLogContent = string.Join("\\n", formattedLines);
                }

                errorLogContent = errorLogContent.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof updateErrorConsole === 'function') {{
                                updateErrorConsole('{errorLogContent}', true);
                            }}
                            console.log('Error log sent to UI');
                        }} catch (error) {{
                            console.error('Error updating error log:', error);
                        }}
                    ");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error sending error log to UI: {ex.Message}{Environment.NewLine}");
            }
        }

        private void ClearErrorLog()
        {
            try
            {
                string errorLogPath = @"C:\GFK\errorlog.txt";

                if (File.Exists(errorLogPath))
                {

                    File.WriteAllText(errorLogPath, $"{DateTime.Now}: Error log cleared via UI{Environment.NewLine}");
                }

            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error clearing error log: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task CheckForUpdates(bool showNotification = false)
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for updates. Current version: {APP_VERSION}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{

                            if (typeof updateVersionDisplay === 'function') {{
                                updateVersionDisplay('{APP_VERSION}');
                            }}
                            console.log('Current version set to: {APP_VERSION}');
                        }} catch (error) {{
                            console.error('Error updating version display:', error);
                        }}
                    ");
                }

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);

                    string versionEndpoint = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.Version}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching version from endpoint: {versionEndpoint}{Environment.NewLine}");

                    var response = await client.GetStringAsync(versionEndpoint);

                    dynamic versionData = Newtonsoft.Json.JsonConvert.DeserializeObject(response);
                    string latestVersion = versionData.version;

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Latest version from API: {latestVersion}{Environment.NewLine}");

                    bool updateAvailable = !string.Equals(APP_VERSION, latestVersion);

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{

                                latestVersion = '{latestVersion}';
                                currentVersion = '{APP_VERSION}';

                                const updateAvailable = {(updateAvailable ? "true" : "false")};

                                if (updateAvailable) {{

                                    const indicator = document.getElementById('update-indicator');
                                    if (indicator) indicator.style.display = 'inline-block';

                                    if ({(showNotification ? "true" : "false")} || (typeof autoUpdateEnabled !== 'undefined' && autoUpdateEnabled)) {{
                                        if (typeof showUpdateNotification === 'function') {{
                                            showUpdateNotification('{APP_VERSION}', '{latestVersion}');
                                        }}
                                    }}

                                    console.log('Update available: {APP_VERSION} → {latestVersion}');
                                }} else {{

                                    const indicator = document.getElementById('update-indicator');
                                    if (indicator) indicator.style.display = 'none';

                                    if ({(showNotification ? "true" : "false")}) {{
                                    }}

                                    console.log('No updates available. Current version: {APP_VERSION}');
                                }}
                            }} catch (error) {{
                                console.error('Error handling version comparison:', error);
                            }}
                        ");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking for updates: {ex.Message}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            if (typeof updateVersionDisplay === 'function') {{
                                updateVersionDisplay('{APP_VERSION}');
                            }}
                            console.error('Update check failed, but version is still set');
                        }} catch (error) {{
                            console.error('Error updating version display:', error);
                        }}
                    ");
                }
            }
        }

        private async Task DownloadAndInstallUpdateAsync()
        {
            string downloadPath = null;

            try
            {
                // Get update URL from config
                string updateUrl = ApiConfigManager.Config?.UpdateUrl ?? "https://apiurl/api/v3/get/latest.exe";

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Starting update download from: {updateUrl}{Environment.NewLine}");

                // Create download path in temp folder
                string tempFolder = Path.GetTempPath();
                downloadPath = Path.Combine(tempFolder, "SWA_Update.exe");

                // Delete old file if exists
                if (File.Exists(downloadPath))
                {
                    File.Delete(downloadPath);
                }

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);

                    // Download the file with progress tracking
                    using (var response = await client.GetAsync(updateUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes > 0;

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Download started. File size: {totalBytes} bytes{Environment.NewLine}");

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;
                            int lastProgress = 0;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (canReportProgress)
                                {
                                    int currentProgress = (int)((totalRead * 100) / totalBytes);

                                    // Report every 10% change
                                    if (currentProgress - lastProgress >= 10 || currentProgress == 100)
                                    {
                                        lastProgress = currentProgress;

                                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                                            $"{DateTime.Now}: Download progress: {currentProgress}%{Environment.NewLine}");

                                        // Update UI progress
                                        this.BeginInvoke((MethodInvoker)(async () =>
                                        {
                                            try
                                            {
                                                if (webView?.CoreWebView2 != null)
                                                {
                                                    await webView.CoreWebView2.ExecuteScriptAsync(
                                                        $"if (typeof updateDownloadProgress === 'function') {{ updateDownloadProgress({currentProgress}); }}");
                                                }
                                            }
                                            catch { }
                                        }));
                                    }
                                }
                            }
                        }
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Download completed successfully: {downloadPath}{Environment.NewLine}");

                // Verify the file exists and has content
                if (!File.Exists(downloadPath))
                {
                    throw new FileNotFoundException("Downloaded file not found");
                }

                var fileInfo = new FileInfo(downloadPath);
                if (fileInfo.Length == 0)
                {
                    throw new Exception("Downloaded file is empty");
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Downloaded file verified. Size: {fileInfo.Length} bytes{Environment.NewLine}");

                // Start the installer
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Launching installer: {downloadPath}{Environment.NewLine}");

                System.Diagnostics.Process.Start(downloadPath);

                // Wait a moment to ensure installer starts
                await Task.Delay(1000);

                // Exit the application
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Exiting application for update...{Environment.NewLine}");

                this.Invoke((MethodInvoker)(() =>
                {
                    Application.Exit();
                }));
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Update download failed: {ex.Message}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Stack trace: {ex.StackTrace}{Environment.NewLine}");

                // Show error to user
                this.BeginInvoke((MethodInvoker)(async () =>
                {
                    try
                    {
                        if (webView?.CoreWebView2 != null)
                        {
                            string errorMsg = ex.Message.Replace("'", "\\'").Replace("\"", "\\\"");
                            await webView.CoreWebView2.ExecuteScriptAsync(
                                $"if (typeof updateDownloadError === 'function') {{ updateDownloadError('{errorMsg}'); }}");
                        }
                    }
                    catch { }
                }));

                // Clean up on error
                if (downloadPath != null && File.Exists(downloadPath))
                {
                    try
                    {
                        File.Delete(downloadPath);
                    }
                    catch { }
                }
            }
        }

        private async Task GetPatchNotesAsync()
        {
            try
            {

                string patchNotesEndpoint = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.PatchNotes}";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching patch notes from endpoint: {patchNotesEndpoint}{Environment.NewLine}");

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetStringAsync(patchNotesEndpoint);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Patch notes response: {response}{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{

                                const patchNotesData = {response};

                                if (typeof updatePatchNotes === 'function') {{
                                    updatePatchNotes(patchNotesData);
                                }}

                                console.log('Patch notes updated successfully');
                            }} catch (error) {{
                                console.error('Error updating patch notes:', error);
                            }}
                        ");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error fetching patch notes: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task GetBannerDataAsync()
        {
            try
            {
                string bannerEndpoint = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.Banner}";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching banner data from endpoint: {bannerEndpoint}{Environment.NewLine}");

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetStringAsync(bannerEndpoint);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Banner data response: {response}{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                const bannerData = {response};

                                if (typeof updateBannerData === 'function') {{
                                    updateBannerData(bannerData);
                                }}

                                console.log('Banner data updated successfully');
                            }} catch (error) {{
                                console.error('Error updating banner data:', error);
                            }}
                        ");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error fetching banner data: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SelectCustomSteamPath()
        {
            try
            {
                using (var folderBrowser = new System.Windows.Forms.FolderBrowserDialog())
                {
                    folderBrowser.Description = "Select Steam installation directory";
                    folderBrowser.ShowNewFolderButton = false;

                    if (folderBrowser.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        string selectedPath = folderBrowser.SelectedPath;

                        // Validate that this is a Steam directory by checking for steam.exe
                        string steamExePath = Path.Combine(selectedPath, "steam.exe");
                        if (!File.Exists(steamExePath))
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Invalid Steam path - steam.exe not found at: {selectedPath}{Environment.NewLine}");

                            // Show error message to user
                            if (webView != null && webView.CoreWebView2 != null)
                            {
                                webView.CoreWebView2.ExecuteScriptAsync(@"
                                    if (typeof showNotification === 'function') {
                                        showNotification('Invalid Steam path - steam.exe not found in selected directory', 'error');
                                    }
                                ");
                            }
                            return;
                        }

                        SaveSteamPath(selectedPath);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Custom Steam path selected and validated: {selectedPath}{Environment.NewLine}");

                        // Update UI
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            webView.CoreWebView2.ExecuteScriptAsync($@"updateSteamPathDisplay('{selectedPath.Replace("\\", "\\\\")}')");
                        }

                        // Check plugin status in the new Steam path
                        CheckSteamPlugin(selectedPath);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Triggered plugin check for new Steam path{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error selecting Steam path: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SaveSteamPath(string path)
        {
            try
            {
                string configPath = @"C:\GFK";
                string settingsFile = Path.Combine(configPath, "steam_settings.json");

                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                }

                var settings = LoadSteamSettingsFromFile();
                settings["steamPath"] = path;

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(settingsFile, json);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Saved Steam path to settings: {path}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error saving Steam path: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SaveSteamPathManual(string path)
        {
            try
            {
                // Validate that steam.exe exists in the path
                string steamExePath = Path.Combine(path, "steam.exe");
                if (!File.Exists(steamExePath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Invalid Steam path (manual) - steam.exe not found at: {path}{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.ExecuteScriptAsync(@"
                            if (typeof showNotification === 'function') {
                                showNotification('Invalid Steam path - steam.exe not found in selected directory', 'error');
                            }
                        ");
                    }
                    return;
                }

                SaveSteamPath(path);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Manual Steam path validated and saved: {path}{Environment.NewLine}");

                // Update UI
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        updateSteamPathDisplay('{path.Replace("\\", "\\\\")}');
                        if (typeof showNotification === 'function') {{
                            showNotification('Steam path saved successfully', 'success');
                        }}
                    ");
                }

                // Check plugin status in the new Steam path
                CheckSteamPlugin(path);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error saving manual Steam path: {ex.Message}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        if (typeof showNotification === 'function') {{
                            showNotification('Error saving Steam path: {ex.Message.Replace("'", "\\'")}', 'error');
                        }}
                    ");
                }
            }
        }

        private void OpenSteamConfigFile()
        {
            try
            {
                string configPath = @"C:\GFK";
                string settingsFile = Path.Combine(configPath, "steam_settings.json");

                // Create the file if it doesn't exist
                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                }

                if (!File.Exists(settingsFile))
                {
                    var defaultSettings = LoadSteamSettingsFromFile();
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(defaultSettings, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(settingsFile, json);
                }

                // Open the file with default editor
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = settingsFile,
                    UseShellExecute = true
                });

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Opened Steam config file: {settingsFile}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening Steam config file: {ex.Message}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        if (typeof showNotification === 'function') {{
                            showNotification('Error opening config file: {ex.Message.Replace("'", "\\'")}', 'error');
                        }}
                    ");
                }
            }
        }

        private void SavePreferredExecutable(string executable)
        {
            try
            {
                string configPath = @"C:\GFK";
                string settingsFile = Path.Combine(configPath, "steam_settings.json");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SavePreferredExecutable called with: {executable}{Environment.NewLine}");

                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                }

                var settings = LoadSteamSettingsFromFile();
                settings["preferredExecutable"] = executable;

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(settingsFile, json);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Saved settings to {settingsFile}: {json}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error saving preferred executable: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void RepairPlugin()
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: RepairPlugin called{Environment.NewLine}");

                // Get Steam path
                string steamPath = GetCurrentSteamPath();

                if (string.IsNullOrEmpty(steamPath) || steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot repair plugin - invalid Steam path: {steamPath}{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            if (typeof showNotification === 'function') {
                                showNotification('Cannot install plugin - Steam path not found. Please select a valid Steam directory.', 'error');
                            }
                        ");
                    }
                    return;
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Attempting to repair plugin for Steam path: {steamPath}{Environment.NewLine}");

                // Download and install plugin
                string pluginUrl = "https://apiurl/api/v3/get/SWAV2_installer.zip";
                await DownloadAndInstallPluginAsync(steamPath, pluginUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error repairing plugin: {ex.Message}{Environment.NewLine}");

                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        if (typeof showNotification === 'function') {{
                            showNotification('Error installing plugin: {ex.Message.Replace("'", "\\'")}', 'error');
                        }}
                    ");
                }
            }
        }

        private async void AutoInstallPluginComponents(string steamPath, bool hidDllExists, bool pluginFolderExists)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: AutoInstallPluginComponents called for: {steamPath}{Environment.NewLine}");

                // Download and install plugin from URL
                string pluginUrl = "https://apiurl/api/v3/get/SWAV2_installer.zip";
                await DownloadAndInstallPluginAsync(steamPath, pluginUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in AutoInstallPluginComponents: {ex.Message}{Environment.NewLine}");
            }
        }

        private bool IsRunningAsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private bool CanWriteToDirectory(string path)
        {
            try
            {
                string testFile = Path.Combine(path, $"_swa_test_{Guid.NewGuid()}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool RestartAsAdministrator()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Verb = "runas",
                    UseShellExecute = true
                };

                Process.Start(startInfo);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restarted app with admin privileges{Environment.NewLine}");

                Application.Exit();
                return true;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User cancelled UAC prompt or error restarting: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private static bool hasRequestedElevation = false;

        private async Task DownloadAndInstallPluginAsync(string steamPath, string pluginUrl)
        {
            string tempZipPath = Path.Combine(@"C:\GFK", "SWAV2_installer.zip");

            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Installing plugin to: {steamPath}{Environment.NewLine}");

                // Ensure C:\GFK directory exists
                if (!Directory.Exists(@"C:\GFK"))
                {
                    Directory.CreateDirectory(@"C:\GFK");
                }

                // Delete old zip if exists
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                // Download the plugin zip
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);
                    client.DefaultRequestHeaders.Add("User-Agent", "SWA-Installer/1.0");

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloading from {pluginUrl}...{Environment.NewLine}");
                    var response = await client.GetAsync(pluginUrl);
                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(tempZipPath, content);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloaded {content.Length} bytes{Environment.NewLine}");
                }

                // Wait for antivirus
                await System.Threading.Tasks.Task.Delay(1000);

                // Extract ALL files and folders from zip directly to Steam folder
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracting all files and folders to Steam folder...{Environment.NewLine}");

                using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        // Build destination path
                        string destPath = Path.Combine(steamPath, entry.FullName.Replace('/', '\\'));

                        // Check if this is a directory entry (ends with / or \ or has no name)
                        if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            // This is a directory - create it
                            if (!Directory.Exists(destPath))
                            {
                                Directory.CreateDirectory(destPath);
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Created directory {destPath}{Environment.NewLine}");
                            }
                        }
                        else
                        {
                            // This is a file - extract it
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracting {entry.FullName} to {destPath}{Environment.NewLine}");

                            // Create parent directory if needed
                            string dirPath = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(dirPath))
                            {
                                Directory.CreateDirectory(dirPath);
                            }

                            // Delete existing file if present
                            if (File.Exists(destPath))
                            {
                                File.SetAttributes(destPath, FileAttributes.Normal);
                                File.Delete(destPath);
                            }

                            // Extract the file
                            entry.ExtractToFile(destPath, true);
                        }
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All files and folders extracted successfully{Environment.NewLine}");

                // Cleanup zip file
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin installed successfully{Environment.NewLine}");

                // Show success notification
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        if (typeof showNotification === 'function') {
                            showNotification('Plugin installed successfully!', 'success');
                        }
                    ");
                }

                // Re-check plugin status
                await System.Threading.Tasks.Task.Delay(500);
                CheckSteamPlugin(steamPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Access denied installing plugin: {ex.Message}{Environment.NewLine}");

                // Only request elevation once
                if (!hasRequestedElevation && !IsRunningAsAdministrator())
                {
                    hasRequestedElevation = true;

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            if (typeof showNotification === 'function') {
                                showNotification('Admin privileges required. Restarting app...', 'info');
                            }
                        ");
                        await System.Threading.Tasks.Task.Delay(1500);
                    }

                    RestartAsAdministrator();
                }
                else
                {
                    // Already tried elevating, show error
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            if (typeof showNotification === 'function') {
                                showNotification('Failed to install plugin: Access denied', 'error');
                            }
                        ");
                    }
                }

                // Cleanup
                try
                {
                    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                }
                catch { }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error installing plugin: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                // Show error notification
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        if (typeof showNotification === 'function') {{
                            showNotification('Error installing plugin: {ex.Message.Replace("'", "\\'")}', 'error');
                        }}
                    ");
                }

                // Cleanup
                try
                {
                    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                }
                catch { }
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private string GetCurrentSteamPath()
        {
            try
            {
                var settings = LoadSteamSettingsFromFile();

                // Check if custom path is set
                if (settings.ContainsKey("steamPath") && !string.IsNullOrEmpty(settings["steamPath"]))
                {
                    return settings["steamPath"];
                }

                // Otherwise return detected Steam path
                return GetSteamPathFromRegistry();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting current Steam path: {ex.Message}{Environment.NewLine}");
                return GetSteamPathFromRegistry();
            }
        }

        private Dictionary<string, string> LoadSteamSettingsFromFile()
        {
            try
            {
                string configPath = @"C:\GFK";
                string settingsFile = Path.Combine(configPath, "steam_settings.json");

                if (File.Exists(settingsFile))
                {
                    string json = File.ReadAllText(settingsFile);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading Steam settings: {ex.Message}{Environment.NewLine}");
            }

            return new Dictionary<string, string>
            {
                { "steamPath", "" },
                { "preferredExecutable", "swav2.exe" }
            };
        }

        private void LoadSteamSettings()
        {
            try
            {
                var settings = LoadSteamSettingsFromFile();
                string settingsJson = Newtonsoft.Json.JsonConvert.SerializeObject(settings);

                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            const settings = {settingsJson};
                            if (typeof loadSteamSettings === 'function') {{
                                loadSteamSettings(settings);
                            }}
                            console.log('Steam settings loaded:', settings);
                        }} catch (error) {{
                            console.error('Error loading Steam settings:', error);
                        }}
                    ");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading Steam settings to UI: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task UpdateLastUpdateFile(string gameId, string lastUpdate)
        {
            try
            {

                string configPath = @"C:\GFK";
                string filePath = Path.Combine(configPath, "last_update.json");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Creating last_update.json at: {filePath}{Environment.NewLine}");

                if (!Directory.Exists(configPath))
                {
                    Directory.CreateDirectory(configPath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Created directory: {configPath}{Environment.NewLine}");
                }

                Dictionary<string, string> updates = new Dictionary<string, string>();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Reading existing last_update.json: {json}{Environment.NewLine}");
                    updates = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: last_update.json does not exist, will create new{Environment.NewLine}");
                }

                updates[gameId] = lastUpdate;

                if (updates.Count == 0)
                {
                    updates["last_update"] = "2025-05-24 00-00";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Adding initial last_update entry{Environment.NewLine}");
                }

                string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(updates, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(filePath, updatedJson);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully updated last_update.json with content: {updatedJson}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error updating last_update.json: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
        }

        private async Task ProcessDLCAsync(string gameId, string gameName)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Starting SMART DLC processing for game {gameId}{Environment.NewLine}");
                
                // Notify UI that DLC processing has started
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(true, 'Processing DLC...');");

                var dlcToAdd = new List<int>();
                var dlcToDisable = new List<int>();

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    try
                    {
                        // Try SWA API first
                        string apiUrl = $"https://apiurl/api/v3/fetchdlc/{gameId}";
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching DLC data from {apiUrl}{Environment.NewLine}");
                        
                        var response = await httpClient.GetStringAsync(apiUrl);
                        var apiData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                        // Check if API response indicates unsupported game
                        if (apiData?.Response?.ToString() == "DLC information not available for this game")
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} not supported by SWA API, using Steam API{Environment.NewLine}");
                            await ProcessDLCUsingSteamAPIFixed(gameId, gameName, httpClient);
                            return;
                        }

                        // Check if we have complete data
                        if (apiData?.is_complete == true && apiData?.dlc != null)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA API has complete data for {gameId}{Environment.NewLine}");
                            
                            // Process all DLCs from SWA API
                            foreach (var dlcEntry in apiData.dlc)
                            {
                                string dlcId = dlcEntry.Name;
                                var dlcInfo = dlcEntry.Value;

                                var releaseDate = dlcInfo.release_date;
                                bool comingSoon = releaseDate != null && releaseDate.coming_soon != null && (bool)releaseDate.coming_soon;

                                if (comingSoon)
                                {
                                    dlcToDisable.Add(int.Parse(dlcId));
                                }
                                else
                                {
                                    dlcToAdd.Add(int.Parse(dlcId));
                                }
                            }

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA API processed {dlcToAdd.Count + dlcToDisable.Count} DLCs for {gameName}{Environment.NewLine}");
                        }
                        else
                        {
                            // Incomplete data or no DLC data, use Steam API
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA API data incomplete for {gameId}, using Steam API{Environment.NewLine}");
                            await ProcessDLCUsingSteamAPIFixed(gameId, gameName, httpClient);
                            return;
                        }
                    }
                    catch (Exception apiEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA API failed for {gameId}: {apiEx.Message}, using Steam API{Environment.NewLine}");
                        await ProcessDLCUsingSteamAPIFixed(gameId, gameName, httpClient);
                        return;
                    }
                }

                // Apply DLC changes
                await ApplyDLCChanges(gameId, dlcToAdd, dlcToDisable);

                string completionMessage = $"Game successfully added with {dlcToAdd.Count} DLC{(dlcToAdd.Count != 1 ? "s" : "")} unlocked!";
                await webView.CoreWebView2.ExecuteScriptAsync($"setDlcProcessing(false, '{completionMessage.Replace("'", "\\'")}');");

                // Refresh DLC manager to show new DLCs
                await Task.Delay(1000);
                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    if (typeof loadDLCsFromAPI === 'function') {
                        loadDLCsFromAPI();
                        console.log('DLC manager refreshed after adding new game');
                    }
                ");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DLC processing completed for game {gameId}. Added: {dlcToAdd.Count}, Disabled: {dlcToDisable.Count}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing DLC for game {gameId}: {ex.Message}{Environment.NewLine}");
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'Game added successfully! (DLC processing error)');");
            }
        }

        private async Task ProcessDLCUsingSteamAPIFixed(string gameId, string gameName, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using Steam API fallback for DLC processing{Environment.NewLine}");
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(true, 'Using Steam API for DLC processing...');");

                // Get DLC list from Steam API
                var dlcList = await GetDLCListFromSteam(gameId, httpClient);
                if (dlcList == null || dlcList.Count == 0)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLC found on Steam for game {gameId}{Environment.NewLine}");
                    await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'Game added successfully! (No DLC found)');");
                    return;
                }

                var dlcToAdd = new List<int>();
                var dlcToDisable = new List<int>();

                for (int i = 0; i < dlcList.Count; i++)
                {
                    int dlcId = dlcList[i];
                    
                    // Update progress
                    string progressMessage = $"Processing DLC for {gameName}... {i + 1}/{dlcList.Count}";
                    await webView.CoreWebView2.ExecuteScriptAsync($"setDlcProcessing(true, '{progressMessage.Replace("'", "\\'")}');");

                    bool comingSoon = await IsDLCComingSoonSteam(dlcId, httpClient);
                    if (comingSoon)
                    {
                        dlcToDisable.Add(dlcId);
                    }
                    else
                    {
                        dlcToAdd.Add(dlcId);
                    }

                    // Rate limiting
                    await Task.Delay(25);
                }

                // Apply DLC changes
                await ApplyDLCChanges(gameId, dlcToAdd, dlcToDisable);

                string completionMessage = $"Game successfully added with {dlcToAdd.Count} DLC{(dlcToAdd.Count != 1 ? "s" : "")} unlocked via Steam API!";
                await webView.CoreWebView2.ExecuteScriptAsync($"setDlcProcessing(false, '{completionMessage.Replace("'", "\\'")}');");

                // Refresh DLC manager to show new DLCs
                await Task.Delay(1000);
                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    if (typeof loadDLCsFromAPI === 'function') {
                        loadDLCsFromAPI();
                        console.log('DLC manager refreshed after Steam API processing');
                    }
                ");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam API DLC processing completed. Added: {dlcToAdd.Count}, Disabled: {dlcToDisable.Count}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in Steam API DLC processing: {ex.Message}{Environment.NewLine}");
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'Game added successfully! (Steam API DLC processing failed)');");
            }
        }

        private async Task<List<int>> GetDLCListFromSteam(string gameId, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={gameId}&cc=us&l=en";
                var response = await httpClient.GetStringAsync(url);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                if (data[gameId]["success"] != null && (bool)data[gameId]["success"])
                {
                    var dlcArray = data[gameId]["data"]["dlc"];
                    if (dlcArray != null)
                    {
                        var dlcList = new List<int>();
                        foreach (var dlc in dlcArray)
                        {
                            dlcList.Add((int)dlc);
                        }
                        return dlcList;
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting DLC list from Steam: {ex.Message}{Environment.NewLine}");
            }
            return new List<int>();
        }

        private async Task<bool> IsDLCComingSoonSteam(int dlcId, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={dlcId}&cc=us&l=en";
                var response = await httpClient.GetStringAsync(url);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                if (data[dlcId.ToString()]["success"] != null && (bool)data[dlcId.ToString()]["success"])
                {
                    var releaseDate = data[dlcId.ToString()]["data"]["release_date"];
                    if (releaseDate != null && releaseDate["coming_soon"] != null)
                    {
                        return (bool)releaseDate["coming_soon"];
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking DLC coming soon status: {ex.Message}{Environment.NewLine}");
            }
            return false;
        }

        private async Task<dynamic> FetchDLCFromAPI(string gameId)
        {
            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    string apiUrl = $"https://apiurl/api/v3/fetchdlc/{gameId}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching DLC data from {apiUrl}{Environment.NewLine}");

                    var response = await httpClient.GetStringAsync(apiUrl);
                    var apiData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
                    return apiData;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error fetching DLC data from API: {ex.Message}{Environment.NewLine}");
                return null;
            }
        }

        private async Task<dynamic> WaitForBackgroundProcessing(string gameId, string gameName)
        {
            int maxAttempts = 30;
            int attempt = 0;

            while (attempt < maxAttempts)
            {
                try
                {
                    var data = await FetchDLCFromAPI(gameId);
                    if (data == null) break;

                    if (data.is_complete != null && (bool)data.is_complete)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'DLC data processing completed');");
                        return data;
                    }

                    var bgInfo = data.background_processing;
                    if (bgInfo != null)
                    {
                        int total = bgInfo.total != null ? (int)bgInfo.total : 0;
                        if (total == 0)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLC data to process, switching to Steam API{Environment.NewLine}");
                            break;
                        }

                        int processed = bgInfo.processed != null ? (int)bgInfo.processed : 0;
                        int progress = bgInfo.progress_percentage != null ? (int)bgInfo.progress_percentage : 0;
                        string status = bgInfo.status != null ? bgInfo.status.ToString() : "unknown";

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DLC processing status: {status} | Progress: {progress}% | Processed: {processed}/{total}{Environment.NewLine}");
                        
                        string progressMessage = $"Processing DLC for {gameName}... {progress}%";
                        await webView.CoreWebView2.ExecuteScriptAsync($"setDlcProcessing(true, '{progressMessage.Replace("'", "\\'")}');");
                    }

                    await Task.Delay(2000);
                    attempt++;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error waiting for background processing: {ex.Message}{Environment.NewLine}");
                    break;
                }
            }

            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Background processing timeout or no data{Environment.NewLine}");
            return null;
        }

        private async Task ProcessDLCUsingSteamAPI(string gameId, string gameName)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using Steam API fallback for DLC processing{Environment.NewLine}");
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(true, 'Using Steam API for DLC processing...');");

                // Get DLC list from Steam API
                var dlcList = await GetDLCListFromSteam(gameId);
                if (dlcList == null || dlcList.Count == 0)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLC found on Steam for game {gameId}{Environment.NewLine}");
                    await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'Game added successfully! (No DLC found)');");
                    return;
                }

                var dlcToAdd = new List<int>();
                var dlcToDisable = new List<int>();

                for (int i = 0; i < dlcList.Count; i++)
                {
                    int dlcId = dlcList[i];
                    await webView.CoreWebView2.ExecuteScriptAsync($"updateDlcProgress({i + 1}, {dlcList.Count}, '{gameName}');");
                    
                    bool comingSoon = await IsDLCComingSoonSteam(dlcId);
                    if (comingSoon)
                    {
                        dlcToDisable.Add(dlcId);
                    }
                    else
                    {
                        dlcToAdd.Add(dlcId);
                    }

                    // Minimal rate limiting for Steam API
                    await Task.Delay(25);
                }

                // Apply DLC changes
                await ApplyDLCChanges(gameId, dlcToAdd, dlcToDisable);
                
                string completionMessage = $"Game successfully added with {dlcToAdd.Count} DLC{(dlcToAdd.Count != 1 ? "s" : "")} unlocked via Steam API!";
                await webView.CoreWebView2.ExecuteScriptAsync($"setDlcProcessing(false, '{completionMessage.Replace("'", "\\'")}');");
                
                // Refresh DLC manager to show new DLCs
                await Task.Delay(1000);
                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    if (typeof loadDLCsFromAPI === 'function') {
                        loadDLCsFromAPI();
                        console.log('DLC manager refreshed after Steam API processing');
                    }
                ");
                
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam API DLC processing completed. Added: {dlcToAdd.Count}, Disabled: {dlcToDisable.Count}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in Steam API DLC processing: {ex.Message}{Environment.NewLine}");
                await webView.CoreWebView2.ExecuteScriptAsync("setDlcProcessing(false, 'Game added successfully! (Steam API DLC processing failed)');");
            }
        }

        private async Task<(int, int)> ProcessDLCData(dynamic apiData, string gameId, string gameName)
        {
            try
            {
                var dlcData = apiData.dlc;
                if (dlcData == null)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLC data in API response{Environment.NewLine}");
                    return (0, 0);
                }

                var dlcToAdd = new List<int>();
                var dlcToDisable = new List<int>();

                foreach (var dlcEntry in dlcData)
                {
                    string dlcId = dlcEntry.Name;
                    var dlcInfo = dlcEntry.Value;

                    var releaseDate = dlcInfo.release_date;
                    bool comingSoon = releaseDate != null && releaseDate.coming_soon != null && (bool)releaseDate.coming_soon;

                    if (comingSoon)
                    {
                        dlcToDisable.Add(int.Parse(dlcId));
                    }
                    else
                    {
                        dlcToAdd.Add(int.Parse(dlcId));
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processed {dlcToAdd.Count + dlcToDisable.Count} DLC items for game {gameId}{Environment.NewLine}");

                // Apply DLC changes
                await ApplyDLCChanges(gameId, dlcToAdd, dlcToDisable);
                
                return (dlcToAdd.Count, dlcToDisable.Count);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing DLC data: {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        private async Task<List<int>> GetDLCListFromSteam(string gameId)
        {
            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    string url = $"https://store.steampowered.com/api/appdetails?appids={gameId}&cc=us&l=en";
                    var response = await httpClient.GetStringAsync(url);
                    var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
                    
                    if (data[gameId]["success"] != null && (bool)data[gameId]["success"])
                    {
                        var dlcArray = data[gameId]["data"]["dlc"];
                        if (dlcArray != null)
                        {
                            var dlcList = new List<int>();
                            foreach (var dlc in dlcArray)
                            {
                                dlcList.Add((int)dlc);
                            }
                            return dlcList;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting DLC list from Steam: {ex.Message}{Environment.NewLine}");
            }
            return new List<int>();
        }

        private async Task<bool> IsDLCComingSoonSteam(int dlcId)
        {
            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    string url = $"https://store.steampowered.com/api/appdetails?appids={dlcId}&cc=us&l=en";
                    var response = await httpClient.GetStringAsync(url);
                    var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
                    
                    if (data[dlcId.ToString()]["success"] != null && (bool)data[dlcId.ToString()]["success"])
                    {
                        var releaseDate = data[dlcId.ToString()]["data"]["release_date"];
                        if (releaseDate != null && releaseDate["coming_soon"] != null)
                        {
                            return (bool)releaseDate["coming_soon"];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking DLC coming soon status: {ex.Message}{Environment.NewLine}");
            }
            return false;
        }

        private async Task ApplyDLCChanges(string gameId, List<int> dlcToAdd, List<int> dlcToDisable)
        {
            try
            {
                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found for DLC processing{Environment.NewLine}");
                    return;
                }

                // Update Steamtools.lua
                if (dlcToAdd.Count > 0)
                {
                    UpdateSteamToolsLua(dlcToAdd, steamPath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Added {dlcToAdd.Count} DLC to Steamtools.lua{Environment.NewLine}");
                }

                // Update appmanifest DisabledDLC
                if (dlcToDisable.Count > 0)
                {
                    UpdateAppManifestDisabledDLC(gameId, dlcToDisable, steamPath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Disabled {dlcToDisable.Count} DLC in appmanifest{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error applying DLC changes: {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        private void UpdateSteamToolsLua(List<int> dlcIds, string steamPath)
        {
            try
            {
                string steamToolsPath = Path.Combine(steamPath, "config", "stplug-in", "Steamtools.lua");
                
                List<string> lines;
                if (File.Exists(steamToolsPath))
                {
                    lines = File.ReadAllLines(steamToolsPath).ToList();
                }
                else
                {
                    lines = new List<string> { "-- Steamtools.lua auto-generated" };
                    Directory.CreateDirectory(Path.GetDirectoryName(steamToolsPath));
                }

                // Remove existing entries for these DLCs
                lines = lines.Where(line => !dlcIds.Any(dlc => line.Contains($"addappid({dlc},"))).ToList();

                // Add new entries
                foreach (int dlc in dlcIds)
                {
                    lines.Add($"addappid({dlc}, 1)");
                }

                File.WriteAllLines(steamToolsPath, lines);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error updating Steamtools.lua: {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        private void UpdateAppManifestDisabledDLC(string gameId, List<int> disabledDlcs, string steamPath)
        {
            try
            {
                string manifestPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{gameId}.acf");
                
                if (!File.Exists(manifestPath))
                {
                    // Create empty appmanifest if it doesn't exist
                    CreateEmptyAppManifest(gameId, steamPath);
                }

                // Read and modify manifest (simplified VDF handling)
                var lines = File.ReadAllLines(manifestPath).ToList();
                bool foundUserConfig = false;
                bool foundDisabledDLC = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Contains("\"UserConfig\""))
                    {
                        foundUserConfig = true;
                    }
                    else if (foundUserConfig && lines[i].Contains("\"DisabledDLC\""))
                    {
                        foundDisabledDLC = true;
                        string disabledDlcValue = string.Join(",", disabledDlcs);
                        lines[i] = $"\t\t\"DisabledDLC\"\t\t\"{disabledDlcValue}\"";
                        break;
                    }
                }

                if (!foundDisabledDLC && foundUserConfig)
                {
                    // Add DisabledDLC entry
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Contains("\"UserConfig\""))
                        {
                            string disabledDlcValue = string.Join(",", disabledDlcs);
                            lines.Insert(i + 2, $"\t\t\"DisabledDLC\"\t\t\"{disabledDlcValue}\"");
                            break;
                        }
                    }
                }

                File.WriteAllLines(manifestPath, lines);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error updating appmanifest: {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        private void CreateEmptyAppManifest(string gameId, string steamPath)
        {
            try
            {
                string steamAppsDir = Path.Combine(steamPath, "steamapps");
                Directory.CreateDirectory(steamAppsDir);
                
                string manifestPath = Path.Combine(steamAppsDir, $"appmanifest_{gameId}.acf");
                
                var template = new[]
                {
                    "\"AppState\"",
                    "{",
                    $"\t\"appid\"\t\t\"{gameId}\"",
                    "\t\"UserConfig\"",
                    "\t{",
                    "\t\t\"DisabledDLC\"\t\t\"\"",
                    "\t}",
                    "}"
                };

                File.WriteAllLines(manifestPath, template);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Created empty appmanifest for game {gameId}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error creating empty appmanifest: {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        private async Task<(int, int)> GetDLCCounts(string gameId)
        {
            try
            {
                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    return (0, 0);
                }

                int addedCount = 0;
                int disabledCount = 0;

                // Count DLCs in Steamtools.lua
                string steamToolsPath = Path.Combine(steamPath, "config", "stplug-in", "Steamtools.lua");
                if (File.Exists(steamToolsPath))
                {
                    var lines = File.ReadAllLines(steamToolsPath);
                    addedCount = lines.Count(line => line.Contains("addappid(") && line.Contains(", 1)"));
                }

                // Count disabled DLCs in appmanifest
                string manifestPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{gameId}.acf");
                if (File.Exists(manifestPath))
                {
                    var lines = File.ReadAllLines(manifestPath);
                    var disabledLine = lines.FirstOrDefault(line => line.Contains("\"DisabledDLC\""));
                    if (disabledLine != null)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(disabledLine, "\"([^\"]+)\"$");
                        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                        {
                            disabledCount = match.Groups[1].Value.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).Count();
                        }
                    }
                }

                return (addedCount, disabledCount);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting DLC counts: {ex.Message}{Environment.NewLine}");
                return (0, 0);
            }
        }

        private async Task TryAddGameAsync(string gameId, bool isUpdate, bool includeDlc = false, bool bypassWarnings = false)
        {
            try
            {
                string action = isUpdate ? "Updating" : "Adding";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: {action} game {gameId}{Environment.NewLine}");
                await UpdateLogMessage($"{(isUpdate ? "Updating" : "Adding")} game {gameId}...");

                bool isGuest = false;
                string username = null;
                string deviceId = null;
                string uniqueId = null;

                string userDataPath = @"C:\GFK\user_data.json";
                if (File.Exists(userDataPath))
                {
                    try
                    {
                        var json = File.ReadAllText(userDataPath);
                        dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                        username = userData.username;
                        deviceId = userData.device_id;
                        uniqueId = userData.unique_id;

                        if (userData.is_guest != null && (bool)userData.is_guest)
                        {
                            isGuest = true;
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User is a guest, will not send auth headers{Environment.NewLine}");
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using unique_id: {uniqueId} for game request{Environment.NewLine}");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error reading user data: {ex.Message}{Environment.NewLine}");
                    }
                }

                if (string.IsNullOrWhiteSpace(deviceId) && File.Exists(@"C:\GFK\device_id.txt"))
                {
                    deviceId = File.ReadAllText(@"C:\GFK\device_id.txt").Trim();
                }

                string apiUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.GameFetch}{gameId}";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking game existence at: {apiUrl}{Environment.NewLine}");

                using (var client = new System.Net.Http.HttpClient())
                {
                    var gameResponse = await client.GetStringAsync(apiUrl);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server response for game {gameId}: {gameResponse}{Environment.NewLine}");

                    if (gameResponse.Length > 1000000)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Response too large ({gameResponse.Length} bytes), truncating{Environment.NewLine}");
                        gameResponse = "{\"error\":\"Response too large\"}";
                    }

                    dynamic gameInfo = null;
                    try
                    {
                        var settings = new Newtonsoft.Json.JsonSerializerSettings
                        {
                            MaxDepth = 10,
                            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None
                        };
                        gameInfo = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(gameResponse, settings);
                    }
                    catch (Exception jsonEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error parsing JSON: {jsonEx.Message}{Environment.NewLine}");
                        await UpdateLogMessage($"Error processing game data: {jsonEx.Message}", "error");
                        await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                        return;
                    }

                    if (gameInfo != null && gameInfo.name != null && gameInfo.File == "0")
                    {
                        string gameName = gameInfo.name.ToString();
                        await UpdateLogMessage($"Game {gameName} is not available yet", "warning");
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} not available yet (File: 0){Environment.NewLine}");
                        await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                        return;
                    }

                    if (gameInfo != null && gameInfo.File == "1")
                    {

                        string gameName = gameInfo.name?.ToString() ?? gameId;
                        await UpdateLogMessage($"Found game: {gameName}", "success");

                        string access = gameInfo.access?.ToString() ?? "0";
                        if (access == "2" && isGuest)
                        {
                            await UpdateLogMessage($"Game {gameName} requires premium access. Please log in with a premium account.", "warning");
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} requires premium access, user is guest{Environment.NewLine}");
                            await webView.CoreWebView2.ExecuteScriptAsync("showPremiumPopup(); unblockAddButton();");
                            return;
                        }

                        // Check if game name is auto-generated (GAME {id}) - treat as no info
                        bool isAutoGeneratedName = gameName.StartsWith("GAME ", StringComparison.OrdinalIgnoreCase);

                        if (isAutoGeneratedName && !bypassWarnings)
                        {
                            var warningData = new
                            {
                                gameId = gameId,
                                gameName = gameName,
                                noInfo = true,
                                includeDlc = includeDlc,
                                isUpdate = isUpdate
                            };
                            string warningJson = Newtonsoft.Json.JsonConvert.SerializeObject(warningData);
                            await webView.CoreWebView2.ExecuteScriptAsync($"showGameWarningPopup({warningJson}, {isUpdate.ToString().ToLower()});");
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} has auto-generated name - showing no-info warning popup (isUpdate={isUpdate}){Environment.NewLine}");
                            return;
                        }

                        // Check for DRM/Launcher warnings and send to JavaScript (unless bypassing)
                        if (!bypassWarnings)
                        {
                            string drmNotice = gameInfo.drm_notice?.ToString() ?? "";
                            string extUserNotice = gameInfo.ext_user_account_notice?.ToString() ?? "";

                            // Log the values for debugging
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} - DRM Notice: '{drmNotice}', Launcher Notice: '{extUserNotice}'{Environment.NewLine}");

                            bool hasWarnings = !string.IsNullOrEmpty(drmNotice) || !string.IsNullOrEmpty(extUserNotice);

                            if (hasWarnings)
                            {
                                var warningData = new
                                {
                                    gameId = gameId,
                                    gameName = gameName,
                                    drmNotice = drmNotice,
                                    extUserNotice = extUserNotice,
                                    includeDlc = includeDlc,
                                    isUpdate = isUpdate
                                };
                                string warningJson = Newtonsoft.Json.JsonConvert.SerializeObject(warningData);
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Warning JSON: {warningJson}{Environment.NewLine}");

                                // Use proper JavaScript string escaping
                                string jsCode = $"showGameWarningPopup({warningJson}, {isUpdate.ToString().ToLower()});";
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Executing JS: {jsCode} (isUpdate={isUpdate}){Environment.NewLine}");

                                try
                                {
                                    string result = await webView.CoreWebView2.ExecuteScriptAsync(jsCode);
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: JS execution result: {result}{Environment.NewLine}");
                                }
                                catch (Exception jsEx)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: JS execution error: {jsEx.Message}{Environment.NewLine}");
                                }
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} has warnings - popup command sent{Environment.NewLine}");
                                return;
                            }
                            else
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} has no warnings - proceeding with download{Environment.NewLine}");
                            }
                        }

                        string lastUpdate = gameInfo.last_update?.ToString() ?? "2025-05-24 00-00";
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Updating last_update.json for game {gameId} with date {lastUpdate}{Environment.NewLine}");

                        try
                        {
                            await UpdateLastUpdateFile(gameId, lastUpdate);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully called UpdateLastUpdateFile for game {gameId}{Environment.NewLine}");
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in UpdateLastUpdateFile: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
                        }

                        string fetchEndpoint = ApiConfigManager.Config.ApiEndpoints?.GameFetch ?? "/api/v3/fetch/";
                        if (!fetchEndpoint.EndsWith("/")) fetchEndpoint += "/";
                        string fetchUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}{fetchEndpoint}{gameId}";

                        await UpdateLogMessage($"Preparing to download {gameName}...");

                        using (var httpClient = new System.Net.Http.HttpClient())
                        {
                            var fetchResponse = await httpClient.GetAsync(fetchUrl);
                            string content = await fetchResponse.Content.ReadAsStringAsync();
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: TryAddGameAsync({gameId}) fetch response: {content}{Environment.NewLine}");

                            if (isGuest && !string.IsNullOrWhiteSpace(content))
                            {
                                try
                                {
                                    var responseData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(content);
                                    if (responseData != null && responseData.RequiresPremium != null && (bool)responseData.RequiresPremium)
                                    {
                                        string responseMessage = responseData.Response?.ToString() ?? "Rate limit exceeded";
                                        int waitTime = responseData.WaitTime != null ? (int)responseData.WaitTime : 60;

                                        await UpdateLogMessage($"Rate limit: {responseMessage}", "warning");
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Guest user rate limited for {waitTime} seconds: {responseMessage}{Environment.NewLine}");

                                        await StartRateLimitCountdown(waitTime);
                                        await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error parsing rate limit response: {ex.Message}{Environment.NewLine}");
                                }
                            }
                        }

                        // Use custom Steam path if set, otherwise use registry
                        string steamPath = GetCurrentSteamPath();
                        if (steamPath == "Not found" || !Directory.Exists(steamPath))
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot download game file{Environment.NewLine}");
                            await UpdateLogMessage("Error: Steam path not found", "error");
                            await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                            return;
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using Steam path for game download: {steamPath}{Environment.NewLine}");

                        string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                        string fileUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}/api/v3/file/{gameId}.zip";
                        string tempZipPath = Path.Combine(pluginFolderPath, $"{gameId}.zip");
                        string luaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.lua");

                        await UpdateLogMessage($"Downloading {gameName}...");

                        try
                        {
                            bool downloadSuccess = false;
                            string errorResponse = null;
                            System.Net.HttpStatusCode? errorStatusCode = null;

                            bool success = await ExecuteWithElevationAsync(async () =>
                            {
                                // Create plugin folder if it doesn't exist
                                if (!Directory.Exists(pluginFolderPath))
                                {
                                    Directory.CreateDirectory(pluginFolderPath);
                                }

                                using (var httpClient = new System.Net.Http.HttpClient())
                                {
                                    if (!isGuest)
                                    {
                                        if (!string.IsNullOrWhiteSpace(username))
                                        {
                                            httpClient.DefaultRequestHeaders.Add("X-Username", username);
                                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Added X-Username header for game download{Environment.NewLine}");
                                        }
                                        if (!string.IsNullOrWhiteSpace(deviceId))
                                        {
                                            httpClient.DefaultRequestHeaders.Add("X-Hardware-ID", uniqueId);
                                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Added X-Hardware-ID header for game download{Environment.NewLine}");
                                        }
                                        if (!string.IsNullOrWhiteSpace(uniqueId))
                                        {
                                            httpClient.DefaultRequestHeaders.Add("X-Unique-ID", uniqueId);
                                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Added X-Unique-ID header: {uniqueId}{Environment.NewLine}");
                                        }
                                    }
                                    else
                                    {
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No auth headers sent - guest user{Environment.NewLine}");
                                    }

                                    using (var response = await httpClient.GetAsync(fileUrl))
                                    {
                                        if (response.IsSuccessStatusCode)
                                        {
                                            using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                                            {
                                                await response.Content.CopyToAsync(fs);
                                            }
                                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloaded {fileUrl} to {tempZipPath}{Environment.NewLine}");

                                            this.Invoke((MethodInvoker)(() => UpdateLogMessage($"Extracting {gameName}...")));

                                            using (var zip = new System.IO.Compression.ZipArchive(new FileStream(tempZipPath, FileMode.Open), System.IO.Compression.ZipArchiveMode.Read))
                                            {
                                                foreach (var entry in zip.Entries)
                                                {
                                                    if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        entry.ExtractToFile(luaFilePath, true);
                                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracted {entry.FullName} to {luaFilePath}{Environment.NewLine}");
                                                    }
                                                    else if (entry.FullName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        string depotCacheDir = Path.Combine(steamPath, "config", "depotcache");
                                                        if (!Directory.Exists(depotCacheDir))
                                                            Directory.CreateDirectory(depotCacheDir);
                                                        string manifestPath = Path.Combine(depotCacheDir, entry.Name);
                                                        entry.ExtractToFile(manifestPath, true);
                                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracted {entry.FullName} to {manifestPath}{Environment.NewLine}");
                                                    }
                                                }
                                            }
                                            File.Delete(tempZipPath);
                                            downloadSuccess = true;
                                        }
                                        else
                                        {
                                            // Save error response for later processing
                                            errorResponse = await response.Content.ReadAsStringAsync();
                                            errorStatusCode = response.StatusCode;
                                        }
                                    }
                                }
                            }, $"download game {gameId}");

                            if (!success)
                            {
                                // UAC was cancelled or elevation failed - exit early
                                await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                return;
                            }

                            // If download succeeded
                            if (downloadSuccess)
                            {
                                await UpdateLogMessage($"{gameName} successfully added!", "success");

                                // Process DLC if requested
                                if (includeDlc && !isUpdate)
                                {
                                    await ProcessDLCAsync(gameId, gameName);
                                }
                                else
                                {
                                    // Unblock add button if no DLC processing
                                    await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                }
                            }
                            else if (errorStatusCode.HasValue)
                            {
                                // Handle HTTP error response
                                string resp = errorResponse;
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to download {fileUrl}: {errorStatusCode.Value} {resp}{Environment.NewLine}");

                                if ((int)errorStatusCode.Value == 429 && isGuest)
                                {
                                    try
                                    {
                                        var rateLimitData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(resp);
                                        if (rateLimitData != null && rateLimitData.RequiresPremium != null && (bool)rateLimitData.RequiresPremium)
                                        {
                                            string responseMessage = rateLimitData.Response?.ToString() ?? "Please wait before downloading another game";
                                            int waitTime = rateLimitData.WaitTime != null ? (int)rateLimitData.WaitTime : 60;

                                            await UpdateLogMessage($"Rate limit: {responseMessage}", "warning");
                                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Guest user rate limited for {waitTime} seconds: {responseMessage}{Environment.NewLine}");

                                            await StartRateLimitCountdown(waitTime);
                                            await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                            return;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error parsing rate limit response: {ex.Message}{Environment.NewLine}");
                                    }
                                }

                                if (resp.Contains("username") && resp.Contains("required") ||
                                    resp.Contains("hardware") && resp.Contains("required") ||
                                    resp.Contains("authentication") && resp.Contains("required") ||
                                    resp.Contains("premium"))
                                {
                                    if (isGuest)
                                    {
                                        await UpdateLogMessage($"Premium account required to download {gameName}. Please log in with a premium account.", "error");
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Premium required for game {gameId}, user is guest{Environment.NewLine}");
                                        await webView.CoreWebView2.ExecuteScriptAsync("showPremiumPopup(); unblockAddButton();");
                                    }
                                    else
                                    {
                                        await UpdateLogMessage($"Failed to download {gameName}: Authentication required", "error");
                                        await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                    }
                                }
                                else
                                {
                                    await UpdateLogMessage($"Failed to download {gameName}: {errorStatusCode.Value}", "error");
                                    await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error downloading/extracting game file: {ex.Message}{Environment.NewLine}");
                            await UpdateLogMessage($"Error downloading {gameName}: {ex.Message}", "error");
                            await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                        }

                        GetSteamInstallPath();
                    }
                    else
                    {

                        if (gameInfo != null && gameInfo.name != null)
                        {
                            string gameName = gameInfo.name.ToString();
                            string access = gameInfo.access?.ToString() ?? "0";

                            if (access == "2")
                            {
                                if (isGuest)
                                {
                                    await UpdateLogMessage($"Game {gameName} requires premium access. Please log in with a premium account.", "warning");
                                    await webView.CoreWebView2.ExecuteScriptAsync("showPremiumPopup();");
                                }
                                else
                                {
                                    await UpdateLogMessage($"Game {gameName} requires premium access", "warning");
                                    await webView.CoreWebView2.ExecuteScriptAsync("showPremiumPopup();");
                                }
                            }
                            else
                            {
                                await UpdateLogMessage($"Game {gameName} is unavailable", "warning");
                            }
                        }
                        else
                        {
                            // No game info available - show warning popup (unless bypassing)
                            if (!bypassWarnings)
                            {
                                var warningData = new
                                {
                                    gameId = gameId,
                                    gameName = gameId,
                                    noInfo = true,
                                    includeDlc = includeDlc,
                                    isUpdate = isUpdate
                                };
                                string warningJson = Newtonsoft.Json.JsonConvert.SerializeObject(warningData);
                                await webView.CoreWebView2.ExecuteScriptAsync($"showGameWarningPopup({warningJson}, {isUpdate.ToString().ToLower()});");
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} has no info - showing warning popup (isUpdate={isUpdate}){Environment.NewLine}");
                                return;
                            }
                            else
                            {
                                await UpdateLogMessage($"Force adding game {gameId} despite no info...", "warning");
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Bypassing no-info warning for game {gameId}{Environment.NewLine}");
                            }
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} not available for download (File: {gameInfo?.File ?? "null"}, Access: {gameInfo?.access ?? "null"}){Environment.NewLine}");

                        // Unblock add button for all error cases
                        await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Ошибка при добавлении игры {gameId}: {ex.Message}{Environment.NewLine}");

                // Show warning popup for unknown errors (unless bypassing)
                if (!bypassWarnings)
                {
                    var warningData = new
                    {
                        gameId = gameId,
                        gameName = gameId,
                        noInfo = true,
                        includeDlc = includeDlc,
                        isUpdate = isUpdate
                    };
                    string warningJson = Newtonsoft.Json.JsonConvert.SerializeObject(warningData);
                    await webView.CoreWebView2.ExecuteScriptAsync($"showGameWarningPopup({warningJson}, {isUpdate.ToString().ToLower()});");
                }
                else
                {
                    await UpdateLogMessage($"Error adding game {gameId}: {ex.Message}", "error");
                    await webView.CoreWebView2.ExecuteScriptAsync("unblockAddButton();");
                }
            }
        }

        private async Task RemoveGameAndManifest(string gameId)
        {
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: RemoveGameAndManifest CALLED with gameId={gameId}{Environment.NewLine}");
            try
            {
                await UpdateLogMessage($"Removing game {gameId}...");

                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot remove game files{Environment.NewLine}");
                    await UpdateLogMessage("Error: Steam path not found", "error");
                    return;
                }
                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                string filePath = Path.Combine(pluginFolderPath, gameId);
                string luaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.lua");
                string ludaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.luda");

                bool fileDeleted = false;
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {filePath}{Environment.NewLine}");
                    fileDeleted = true;
                }
                else if (File.Exists(luaFilePath))
                {
                    File.Delete(luaFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {luaFilePath}{Environment.NewLine}");
                    fileDeleted = true;
                }
                else if (File.Exists(ludaFilePath))
                {
                    File.Delete(ludaFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {ludaFilePath}{Environment.NewLine}");
                    fileDeleted = true;
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No game file found for {gameId}{Environment.NewLine}");
                    await UpdateLogMessage($"No game file found for {gameId}", "warning");
                }

                if (fileDeleted)
                {
                    await UpdateLogMessage($"Game {gameId} removed successfully", "success");
                    ScanPluginGames(pluginFolderPath);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error removing game {gameId}: {ex.Message}{Environment.NewLine}");
                await UpdateLogMessage($"Error removing game: {ex.Message}", "error");
            }
        }

        private async Task RemoveGameFile(string gameId)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: RemoveGameFile called for game {gameId}{Environment.NewLine}");
                await UpdateLogMessage($"Removing file for game {gameId}...");

                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot remove game file{Environment.NewLine}");
                    await UpdateLogMessage("Error: Steam path not found", "error");
                    return;
                }

                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");

                if (!Directory.Exists(pluginFolderPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin directory does not exist: {pluginFolderPath}, creating it{Environment.NewLine}");
                    Directory.CreateDirectory(pluginFolderPath);
                }

                string luaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.lua");
                string ludaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.luda");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Looking for files at: {luaFilePath} and {ludaFilePath}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Files exist: lua={File.Exists(luaFilePath)}, luda={File.Exists(ludaFilePath)}{Environment.NewLine}");

                bool fileDeleted = false;

                fileDeleted = ExecuteWithElevation(() =>
                {
                    if (File.Exists(luaFilePath))
                    {

                        FileInfo fileInfo = new FileInfo(luaFilePath);
                        if ((fileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        File.Delete(luaFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {luaFilePath}{Environment.NewLine}");
                    }

                    if (File.Exists(ludaFilePath))
                    {

                        FileInfo fileInfo = new FileInfo(ludaFilePath);
                        if ((fileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        File.Delete(ludaFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {ludaFilePath}{Environment.NewLine}");
                    }
                }, $"remove game {gameId}");

                if (!fileDeleted)
                {
                    await UpdateLogMessage($"Failed to remove game {gameId}", "error");
                    return;
                }

                ScanPluginGames(pluginFolderPath);
                await UpdateLogMessage($"File for game {gameId} removed successfully", "success");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in RemoveGameFile: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
                await UpdateLogMessage($"Error removing file for game {gameId}: {ex.Message}", "error");
            }
        }

        private async Task PatchSteamAsync()
        {
            try
            {

                try
                {
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName("steam"))
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Killed process steam.exe (PID: {proc.Id})\n");
                    }
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName("SWAV2"))
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Killed process SWAV2.exe (PID: {proc.Id})\n");
                    }

                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error killing processes: {ex.Message}\n");
                }

                string steamUrl = ApiConfigManager.Config?.SteamUrl;
                if (string.IsNullOrWhiteSpace(steamUrl))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamUrl is empty in config\n");
                    await NotifyPatchSteamResult(false, "Steam patch URL not found in config.");
                    return;
                }

                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot patch\n");
                    await NotifyPatchSteamResult(false, "Steam folder not found.");
                    return;
                }

                string tempZipPath = Path.Combine(Path.GetTempPath(), "steam_patch.zip");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloading Steam patch from {steamUrl} to {tempZipPath}\n");

                using (var httpClient = new System.Net.Http.HttpClient())
                using (var response = await httpClient.GetAsync(steamUrl))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string resp = await response.Content.ReadAsStringAsync();
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to download Steam patch: {response.StatusCode} {resp}\n");
                        await NotifyPatchSteamResult(false, $"Failed to download patch: {response.StatusCode}");
                        return;
                    }
                    using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloaded patch, extracting to {steamPath}\n");
                try
                {
                    using (var zip = new System.IO.Compression.ZipArchive(new FileStream(tempZipPath, FileMode.Open), System.IO.Compression.ZipArchiveMode.Read))
                    {
                        foreach (var entry in zip.Entries)
                        {

                            if (string.IsNullOrEmpty(entry.Name))
                                continue;
                            string destPath = Path.Combine(steamPath, entry.FullName);
                            string destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);
                            entry.ExtractToFile(destPath, true);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracted {entry.FullName} to {destPath}\n");
                        }
                    }
                    File.Delete(tempZipPath);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting patch: {ex.Message}\n{ex.StackTrace}\n");
                    await NotifyPatchSteamResult(false, "Error extracting patch: " + ex.Message);
                    return;
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam patch applied successfully.\n");
                await NotifyPatchSteamResult(true, "Steam patch applied successfully.");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in PatchSteamAsync: {ex.Message}\n{ex.StackTrace}\n");
                await NotifyPatchSteamResult(false, "Error: " + ex.Message);
            }
        }

        private async Task NotifyPatchSteamResult(bool success, string message)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                string color = success ? "#2ecc71" : "#e74c3c";
                string js = $@"
                    try {{
                        if (window && window.Swal) {{
                            Swal.fire({{
                                toast: true,
                                position: 'top-end',
                                icon: '{(success ? "success" : "error")}',
                                title: '{message.Replace("'", "\'")}',
                                showConfirmButton: false,
                                timer: 4000,
                                timerProgressBar: true,
                                background: '#222',
                                color: '#fff',
                                iconColor: '{color}'
                            }});
                        }} else {{
                            alert('{message.Replace("'", "\'")}');
                        }}
                    }} catch (e) {{ alert('{message.Replace("'", "\'")}'); }}
                ";
                await webView.CoreWebView2.ExecuteScriptAsync(js);
            }
        }

        private async Task FetchGameInfoAsync(string gameId)
        {

            bool shouldProceed = false;
            lock (gameInfoLock)
            {
                DateTime lastRequestTime;
                if (gameInfoRequests.TryGetValue(gameId, out lastRequestTime))
                {

                    if ((DateTime.Now - lastRequestTime).TotalSeconds < 5)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Skipping duplicate FetchGameInfoAsync for game {gameId} (last request: {lastRequestTime}){Environment.NewLine}");
                        return;
                    }
                }

                gameInfoRequests[gameId] = DateTime.Now;
                shouldProceed = true;
            }

            if (!shouldProceed) return;

            bool dataProcessedSuccessfully = false;
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Starting FetchGameInfoAsync for game {gameId}{Environment.NewLine}");

                string apiUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}{ApiConfigManager.Config.ApiEndpoints.GameFetch}{gameId}";
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching game info from: {apiUrl}{Environment.NewLine}");

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetStringAsync(apiUrl);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game info response for {gameId}: {response.Substring(0, Math.Min(response.Length, 200))}...{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                if (typeof processGameInfo === 'function') {{
                                    processGameInfo({response}, '{gameId}');
                                    console.log('Game info processed successfully for {gameId}');
                                    window.chrome.webview.postMessage('gameInfoProcessed:{gameId}');
                                }}
                            }} catch (error) {{
                                console.error('Error processing game info:', error);
                            }}
                        ");
                        dataProcessedSuccessfully = true;
                    }
                }
            }
            catch (Exception ex)
            {

                if (!dataProcessedSuccessfully)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error fetching game info for {gameId}: {ex.Message}{Environment.NewLine}");

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                if (typeof processGameInfoError === 'function') {{
                                    processGameInfoError('{gameId}', '{ex.Message.Replace("'", "\\'")}');
                                }}

                                console.error('Error fetching game info for {gameId}: {ex.Message.Replace("'", "\\'")}');
                            }} catch (error) {{
                                console.error('Error handling game info error:', error);
                            }}
                        ");
                    }
                }
            }
        }

        private string GetVolumeSerial(string drive)
        {

            string volumeSerial = "0000-0000";

            try
            {

                using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\MountedDevices"))
                {
                    if (baseKey != null)
                    {
                        string[] valueNames = baseKey.GetValueNames();
                        foreach (string name in valueNames)
                        {
                            if (name.StartsWith(@"\DosDevices\" + drive.TrimEnd('\\')))
                            {
                                byte[] value = (byte[])baseKey.GetValue(name);
                                volumeSerial = BitConverter.ToString(value).Replace("-", "");
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {

                volumeSerial = Environment.MachineName + "-" + Environment.UserName;
            }

            return volumeSerial;
        }

        private async Task DownloadAndPatchSteamAsync()
        {
            try
            {

                string steamPatchUrl = ApiConfigManager.Config?.SteamUrl;
                if (string.IsNullOrWhiteSpace(steamPatchUrl))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamUrl is empty in config\n");
                    return;
                }

                string steamPath = GetSteamPathFromRegistry();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot patch\n");
                    return;
                }

                string tempZipPath = Path.Combine(Path.GetTempPath(), "steam_patch.zip");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloading Steam patch from {steamPatchUrl} to {tempZipPath}\n");

                using (var httpClient = new System.Net.Http.HttpClient())
                using (var response = await httpClient.GetAsync(steamPatchUrl))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string resp = await response.Content.ReadAsStringAsync();
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to download Steam patch: {response.StatusCode} {resp}\n");
                        return;
                    }
                    using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Downloaded patch, extracting to {steamPath}\n");

                bool anyFileExtracted = false;
                int totalFiles = 0;
                int extractedFiles = 0;
                int skippedFiles = 0;

                try
                {
                    using (var zip = new System.IO.Compression.ZipArchive(new FileStream(tempZipPath, FileMode.Open), System.IO.Compression.ZipArchiveMode.Read))
                    {

                        totalFiles = zip.Entries.Count(e => !string.IsNullOrEmpty(e.Name));

                        foreach (var entry in zip.Entries)
                        {
                            try
                            {

                                if (string.IsNullOrEmpty(entry.Name))
                                    continue;

                                string destPath = Path.Combine(steamPath, entry.FullName);
                                string destDir = Path.GetDirectoryName(destPath);

                                if (!string.IsNullOrEmpty(destDir))
                                    Directory.CreateDirectory(destDir);

                                try
                                {
                                    entry.ExtractToFile(destPath, true);
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Extracted {entry.FullName} to {destPath}\n");
                                    extractedFiles++;
                                    anyFileExtracted = true;
                                }
                                catch (IOException ioEx) when (ioEx.Message.Contains("используется другим процессом") ||
                                                              ioEx.Message.Contains("being used by another process"))
                                {

                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting {entry.FullName}: {ioEx.Message}\n");
                                    skippedFiles++;

                                    continue;
                                }
                            }
                            catch (Exception entryEx)
                            {

                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting {entry.FullName}: {entryEx.Message}\n");
                                skippedFiles++;
                            }
                        }
                    }

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Extraction completed. Total files: {totalFiles}, Extracted: {extractedFiles}, Skipped: {skippedFiles}\n");

                    try
                    {
                        File.Delete(tempZipPath);
                    }
                    catch (Exception delEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Warning: Could not delete temp file: {delEx.Message}\n");
                    }

                    if (!anyFileExtracted)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Patch failed - no files could be extracted\n");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting patch: {ex.Message}\n{ex.StackTrace}\n");
                    return;
                }

                try
                {
                    string configPath = @"C:\GFK";
                    string filePath = Path.Combine(configPath, "last_update.json");
                    Dictionary<string, object> updates = new Dictionary<string, object>();
                    if (File.Exists(filePath))
                    {
                        var json = File.ReadAllText(filePath);
                        updates = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
                    }
                    updates["steam_patch"] = 1;
                    string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(updates, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(filePath, updatedJson);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully updated last_update.json with steam_patch: 1\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error updating last_update.json for steam_patch: {ex.Message}\n");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in DownloadAndPatchSteamAsync: {ex.Message}\n{ex.StackTrace}\n");
            }
        }

        private void SetupUserDataMonitoring()
        {
            try
            {
                string userDataPath = @"C:\GFK";
                string userDataFile = "user_data.json";

                if (!Directory.Exists(userDataPath))
                {
                    Directory.CreateDirectory(userDataPath);
                }

                userDataWatcher = new FileSystemWatcher(userDataPath);
                userDataWatcher.Filter = userDataFile;
                userDataWatcher.EnableRaisingEvents = true;
                userDataWatcher.Changed += UserDataWatcher_Changed;
                userDataWatcher.Created += UserDataWatcher_Changed;

// DISABLED:                 userDataCheckTimer = new System.Threading.Timer(CheckUserDataFile, null,
// DISABLED:                     TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: User data monitoring set up for {Path.Combine(userDataPath, userDataFile)}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error setting up user data monitoring: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SetupSteamSettingsMonitoring()
        {
            try
            {
                string settingsPath = @"C:\GFK";
                string settingsFile = "steam_settings.json";

                if (!Directory.Exists(settingsPath))
                {
                    Directory.CreateDirectory(settingsPath);
                }

                steamSettingsWatcher = new FileSystemWatcher(settingsPath);
                steamSettingsWatcher.Filter = settingsFile;
                steamSettingsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                steamSettingsWatcher.EnableRaisingEvents = true;
                steamSettingsWatcher.Changed += SteamSettingsWatcher_Changed;

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Steam settings monitoring set up for {Path.Combine(settingsPath, settingsFile)}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error setting up steam settings monitoring: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SteamSettingsWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            try
            {
                Thread.Sleep(200); // Wait for file to be fully written

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Steam settings file changed, reloading settings{Environment.NewLine}");

                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Reload settings
                        var settings = LoadSteamSettingsFromFile();

                        // Get the Steam path (custom or detected)
                        string steamPath = GetCurrentSteamPath();

                        // Update UI with new path
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            string escapedPath = steamPath.Replace("\\", "\\\\");
                            webView.CoreWebView2.ExecuteScriptAsync($@"
                                try {{
                                    if (typeof updateSteamPathDisplay === 'function') {{
                                        updateSteamPathDisplay('{escapedPath}');
                                    }}
                                    console.log('Steam path updated from settings file: {escapedPath}');
                                }} catch (error) {{
                                    console.error('Error updating Steam path from settings:', error);
                                }}
                            ");
                        }

                        // Check plugin status for the new path
                        if (steamPath != "Not found" && Directory.Exists(steamPath))
                        {
                            CheckSteamPlugin(steamPath);
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Steam settings reloaded successfully. Path: {steamPath}{Environment.NewLine}");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Error processing steam settings change: {ex.Message}{Environment.NewLine}");
                    }
                }));
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in SteamSettingsWatcher_Changed: {ex.Message}{Environment.NewLine}");
            }
        }

        private void UserDataWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            try
            {

                Thread.Sleep(100);

                this.BeginInvoke(new Action(() =>
                {

                    ProcessUserDataFileChange();
                }));
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in file watcher: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void ProcessUserDataFileChange()
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: user_data.json changed, reloading data{Environment.NewLine}");
// DISABLED:                 await RefreshUserDataFromFile();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in file change handler: {ex.Message}{Environment.NewLine}");
            }
        }

        private void StopRestrictionCheckTimer()
        {
            try
            {

                if (restrictionCheckTimer != null)
                {
                    restrictionCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    restrictionCheckTimer.Dispose();
                    restrictionCheckTimer = null;

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Stopped restriction check timer{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error stopping restriction check timer: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task ForceRefreshWebView()
        {
            try
            {

                if (!this.InvokeRequired)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Начата принудительная перезагрузка WebView2 (UI поток){Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Запрос на перезагрузку WebView2 из фонового потока, переключаемся на UI поток{Environment.NewLine}");

                    this.Invoke(new Action(async () =>
                    {
                        await ForceRefreshWebView();
                    }));
                    return;
                }

                try
                {

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            try {

                                if (typeof caches !== 'undefined') {
                                    caches.keys().then(function(names) {
                                        for (let name of names) caches.delete(name);
                                    });
                                }

                                if (document && document.body) {
                                    document.body.style.zoom = '100.1%';
                                    setTimeout(() => { document.body.style.zoom = '100%'; }, 50);
                                }

                                console.log('Cache cleared and DOM refreshed');
                            } catch (error) {
                                console.error('Error refreshing page:', error);
                            }
                        ");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Невозможно очистить кэш: webView или CoreWebView2 равны null{Environment.NewLine}");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Ошибка при очистке кэша: {ex.Message}{Environment.NewLine}");
                }

                try
                {

                    if (webView != null && webView.CoreWebView2 != null)
                    {

                        string currentUrl = webView.CoreWebView2.Source;

                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            try {

                                const overlay = document.getElementById('loading-overlay');
                                if (overlay) {
                                    overlay.classList.add('hidden');
                                    overlay.style.display = 'none';
                                }

                                console.log('Forcing page reload');
                                window.location.reload(true);
                            } catch (error) {
                                console.error('Error reloading page:', error);
                            }
                        ");

                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Страница перезагружена по URL: {currentUrl}{Environment.NewLine}");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Невозможно перезагрузить страницу: webView или CoreWebView2 равны null{Environment.NewLine}");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Ошибка при перезагрузке страницы: {ex.Message}{Environment.NewLine}");

                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                        {
                            webView.CoreWebView2.Reload();
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Страница перезагружена через метод Reload(){Environment.NewLine}");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Невозможно использовать Reload(): webView или CoreWebView2 равны null{Environment.NewLine}");

                            if (webView == null)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: WebView равен null, пробуем пересоздать его{Environment.NewLine}");

                                webView = new WebView2();
                                webView.Dock = DockStyle.Fill;
                                webView.AllowExternalDrop = false;
                                this.Controls.Add(webView);

                                await InitializeWebViewAsync();
                                return;
                            }
                        }
                    }
                    catch (Exception reloadEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Ошибка при использовании Reload(): {reloadEx.Message}{Environment.NewLine}");

                        try
                        {
                            if (webView != null && webView.CoreWebView2 != null)
                            {
                                await LoadDashboardAsync();
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Выполнена полная перезагрузка дашборда{Environment.NewLine}");
                            }
                            else
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                        $"{DateTime.Now}: Невозможно загрузить дашборд: webView или CoreWebView2 равны null{Environment.NewLine}");
                            }
                        }
                        catch (Exception loadEx)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Критическая ошибка при перезагрузке дашборда: {loadEx.Message}{Environment.NewLine}");
                        }
                    }
                }

                webView.Focus();

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Принудительная перезагрузка WebView2 завершена{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
            $"{DateTime.Now}: Общая ошибка при принудительной перезагрузке WebView2: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
        }


        private HashSet<string> ReadInstalledDLCs()
        {
            var installedDLCs = new HashSet<string>();

            try
            {
                // Use custom Steam path if set, otherwise use registry
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found for reading DLCs{Environment.NewLine}");
                    return installedDLCs;
                }

                string steamToolsPath = Path.Combine(steamPath, "config", "stplug-in", "SteamTools.lua");
                if (!File.Exists(steamToolsPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamTools.lua not found at {steamToolsPath}{Environment.NewLine}");
                    return installedDLCs;
                }

                string content = File.ReadAllText(steamToolsPath);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Reading SteamTools.lua from {steamToolsPath}{Environment.NewLine}");

                var lines = content.Split('\n');
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    // Match patterns like addappid(1613280, 1)
                    if (trimmedLine.StartsWith("addappid("))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"addappid\((\d+),\s*1\)");
                        if (match.Success)
                        {
                            string dlcId = match.Groups[1].Value;
                            installedDLCs.Add(dlcId);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found installed DLC: {dlcId}{Environment.NewLine}");
                        }
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found {installedDLCs.Count} installed DLCs{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error reading SteamTools.lua: {ex.Message}{Environment.NewLine}");
            }

            return installedDLCs;
        }

        // Method to remove a specific DLC from SteamTools.lua
        private bool RemoveDLCFromSteamTools(string dlcId)
        {
            try
            {
                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found for removing DLC{Environment.NewLine}");
                    return false;
                }

                string steamToolsLuaPath = Path.Combine(steamPath, "config", "stplug-in", "SteamTools.lua");
                if (!File.Exists(steamToolsLuaPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamTools.lua not found at {steamToolsLuaPath}{Environment.NewLine}");
                    return false;
                }

                // Read all lines from SteamTools.lua
                string[] lines = File.ReadAllLines(steamToolsLuaPath);
                var updatedLines = new List<string>();
                bool dlcRemoved = false;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    
                    // Check if this line contains the DLC we want to remove
                    var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"addappid\((\d+),\s*1\)");
                    if (match.Success && match.Groups[1].Value == dlcId)
                    {
                        // Skip this line (remove it)
                        dlcRemoved = true;
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found and removing line: {line}{Environment.NewLine}");
                        continue;
                    }
                    
                    // Keep all other lines
                    updatedLines.Add(line);
                }

                if (dlcRemoved)
                {
                    // Write the updated content back to SteamTools.lua
                    File.WriteAllLines(steamToolsLuaPath, updatedLines);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed DLC {dlcId} from SteamTools.lua{Environment.NewLine}");
                    return true;
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DLC {dlcId} not found in SteamTools.lua{Environment.NewLine}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception while removing DLC {dlcId} from SteamTools.lua: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        // Method to remove all DLCs from SteamTools.lua
        private bool RemoveAllDLCsFromSteamTools()
        {
            try
            {
                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found for removing all DLCs{Environment.NewLine}");
                    return false;
                }

                string steamToolsLuaPath = Path.Combine(steamPath, "config", "stplug-in", "SteamTools.lua");
                if (!File.Exists(steamToolsLuaPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamTools.lua not found at {steamToolsLuaPath}{Environment.NewLine}");
                    return false;
                }

                // Read all lines from SteamTools.lua
                string[] lines = File.ReadAllLines(steamToolsLuaPath);
                var updatedLines = new List<string>();
                int removedCount = 0;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    
                    // Keep lines that are NOT DLC entries (addappid with , 1))
                    if (!trimmedLine.StartsWith("addappid(") || !trimmedLine.Contains(", 1)"))
                    {
                        updatedLines.Add(line);
                    }
                    else
                    {
                        removedCount++;
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing DLC line: {trimmedLine}{Environment.NewLine}");
                    }
                }

                // Write the updated content back to SteamTools.lua
                File.WriteAllLines(steamToolsLuaPath, updatedLines);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed {removedCount} DLCs from SteamTools.lua{Environment.NewLine}");
                return true;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception while removing all DLCs from SteamTools.lua: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        // Method to remove all DLCs for a specific game from SteamTools.lua (async version using Steam API cache)
        private async Task<bool> RemoveGameDLCsFromSteamToolsAsync(string gameId)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing all DLCs for game {gameId} from SteamTools.lua{Environment.NewLine}");

                // Use custom Steam path if set
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found for removing game DLCs{Environment.NewLine}");
                    return false;
                }

                string steamToolsLuaPath = Path.Combine(steamPath, "config", "stplug-in", "SteamTools.lua");
                if (!File.Exists(steamToolsLuaPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamTools.lua not found at {steamToolsLuaPath}{Environment.NewLine}");
                    return false;
                }

                // Get the list of DLC IDs for this game from Steam API
                var gameDLCIDs = new HashSet<string>();
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);

                    try
                    {
                        // Get DLC list from Steam API (with cache)
                        var gameInfo = await GetGameDlcsFromSteam(gameId, httpClient);

                        if (gameInfo != null && gameInfo.AllDlcIds.Count > 0)
                        {
                            gameDLCIDs = new HashSet<string>(gameInfo.AllDlcIds);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found {gameDLCIDs.Count} DLCs for game {gameId} from Steam API{Environment.NewLine}");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLC information found for game {gameId} from Steam API{Environment.NewLine}");

                            // Fallback: check persistent cache
                            var persistentCache = LoadDlcCacheFromDisk();
                            foreach (var cachedDlc in persistentCache.DlcData.Values)
                            {
                                if (cachedDlc.GameId == gameId)
                                {
                                    gameDLCIDs.Add(cachedDlc.DlcId);
                                }
                            }

                            if (gameDLCIDs.Count > 0)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found {gameDLCIDs.Count} DLCs for game {gameId} from persistent cache{Environment.NewLine}");
                            }
                            else
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLCs found for game {gameId} in cache either{Environment.NewLine}");
                                return false;
                            }
                        }
                    }
                    catch (Exception apiEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to get DLC info from Steam API: {apiEx.Message}{Environment.NewLine}");
                        return false;
                    }
                }

                // Read all lines from SteamTools.lua
                string[] lines = File.ReadAllLines(steamToolsLuaPath);
                var updatedLines = new List<string>();
                var removedDlcIds = new List<string>();
                int removedCount = 0;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    // Check if line is a DLC entry
                    if (trimmedLine.StartsWith("addappid(") && trimmedLine.Contains(", 1)"))
                    {
                        // Extract DLC ID from line
                        var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"addappid\((\d+),\s*1\)");
                        if (match.Success)
                        {
                            string dlcId = match.Groups[1].Value;

                            // If we have a list of DLC IDs for this game, check if this DLC belongs to the game
                            if (gameDLCIDs.Count > 0)
                            {
                                if (gameDLCIDs.Contains(dlcId))
                                {
                                    // This DLC belongs to the game, remove it
                                    removedCount++;
                                    removedDlcIds.Add(dlcId);
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Removing DLC {dlcId} for game {gameId}: {trimmedLine}{Environment.NewLine}");
                                    continue; // Skip adding this line
                                }
                            }
                        }
                    }

                    // Keep all other lines
                    updatedLines.Add(line);
                }

                // Write the updated content back to SteamTools.lua
                File.WriteAllLines(steamToolsLuaPath, updatedLines);

                // Update persistent cache - remove deleted DLCs
                if (removedDlcIds.Count > 0)
                {
                    var persistentCache = LoadDlcCacheFromDisk();

                    foreach (var dlcId in removedDlcIds)
                    {
                        if (persistentCache.DlcData.ContainsKey(dlcId))
                        {
                            persistentCache.DlcData.Remove(dlcId);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🗑️ Removed DLC {dlcId} from persistent cache{Environment.NewLine}");
                        }
                    }

                    // Update LastSteamToolsDlcs list
                    persistentCache.LastSteamToolsDlcs = ReadInstalledDLCs().ToList();
                    SaveDlcCacheToDisk(persistentCache);
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully removed {removedCount} DLCs for game {gameId} from SteamTools.lua and cache{Environment.NewLine}");
                return removedCount > 0;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception while removing game DLCs: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        // Static cache for DLC data to avoid repeated API calls
        private static readonly Dictionary<string, object> _dlcCache = new Dictionary<string, object>();
        private static readonly object _cacheLock = new object();

        // Steam API cache for game->DLC mappings
        private static readonly Dictionary<string, SteamGameDlcInfo> _steamDlcCache = new Dictionary<string, SteamGameDlcInfo>();

        // FileSystemWatcher for monitoring steamtools.lua changes
        private FileSystemWatcher _steamToolsWatcher;
        private System.Threading.Timer _debounceTimer;
        private bool _isProcessingDlcCache = false;

        // Get cache file path based on current Steam path
        private string GetDlcCacheFilePath()
        {
            string steamPath = GetCurrentSteamPath();
            if (steamPath == "Not found" || !Directory.Exists(steamPath))
            {
                // Fallback to GFK folder if Steam path not found
                return @"C:\GFK\dlc_cache.json";
            }

            string cacheDir = Path.Combine(steamPath, "config", "stplug-in");

            // Create directory if it doesn't exist
            if (!Directory.Exists(cacheDir))
            {
                try
                {
                    Directory.CreateDirectory(cacheDir);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to create cache directory: {ex.Message}{Environment.NewLine}");
                    return @"C:\GFK\dlc_cache.json"; // Fallback
                }
            }

            return Path.Combine(cacheDir, "dlc_cache.json");
        }

        // Helper class for Steam DLC info
        private class SteamGameDlcInfo
        {
            public string GameId { get; set; }
            public string GameName { get; set; }
            public List<string> AllDlcIds { get; set; }
            public DateTime CachedAt { get; set; }
        }

        // Helper class for persistent DLC cache
        private class PersistentDlcCache
        {
            public Dictionary<string, CachedDlcInfo> DlcData { get; set; } = new Dictionary<string, CachedDlcInfo>();
            public List<string> LastSteamToolsDlcs { get; set; } = new List<string>();
            public DateTime LastUpdated { get; set; }
        }

        private class CachedDlcInfo
        {
            public string DlcId { get; set; }
            public string GameId { get; set; }
            public string GameName { get; set; }
            public string DlcName { get; set; }
            public string HeaderImage { get; set; }
            public object ReleaseDate { get; set; }
            public DateTime CachedAt { get; set; }
        }

        // Load persistent DLC cache from disk
        private PersistentDlcCache LoadDlcCacheFromDisk()
        {
            try
            {
                string cacheFilePath = GetDlcCacheFilePath();
                if (File.Exists(cacheFilePath))
                {
                    string json = File.ReadAllText(cacheFilePath);
                    var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<PersistentDlcCache>(json);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 💾 Loaded DLC cache from {cacheFilePath}: {cache.DlcData.Count} DLCs cached{Environment.NewLine}");
                    return cache;
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 📂 No cache file found at {cacheFilePath}, creating new cache{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading DLC cache: {ex.Message}{Environment.NewLine}");
            }

            return new PersistentDlcCache();
        }

        // Save persistent DLC cache to disk
        private void SaveDlcCacheToDisk(PersistentDlcCache cache)
        {
            try
            {
                string cacheFilePath = GetDlcCacheFilePath();
                cache.LastUpdated = DateTime.Now;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(cache, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(cacheFilePath, json);
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 💾 Saved DLC cache to {cacheFilePath}: {cache.DlcData.Count} DLCs{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error saving DLC cache: {ex.Message}{Environment.NewLine}");
            }
        }

        // Start monitoring steamtools.lua for changes
        private void StartSteamToolsMonitoring()
        {
            try
            {
                string steamPath = GetCurrentSteamPath();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Cannot start SteamTools monitoring - Steam path not found{Environment.NewLine}");
                    return;
                }

                string pluginDir = Path.Combine(steamPath, "config", "stplug-in");
                if (!Directory.Exists(pluginDir))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SteamTools monitoring - plugin directory doesn't exist yet{Environment.NewLine}");
                    return;
                }

                // Dispose existing watcher if any
                _steamToolsWatcher?.Dispose();

                // Create new file watcher
                _steamToolsWatcher = new FileSystemWatcher(pluginDir)
                {
                    Filter = "SteamTools.lua",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                // Handle file changes with debouncing
                _steamToolsWatcher.Changed += OnSteamToolsChanged;
                _steamToolsWatcher.Created += OnSteamToolsChanged;

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 👁️ Started monitoring SteamTools.lua at {pluginDir}{Environment.NewLine}");

                // Run initial background cache update
                Task.Run(() => ProcessDlcCacheInBackground());
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error starting SteamTools monitoring: {ex.Message}{Environment.NewLine}");
            }
        }

        // Handle steamtools.lua file changes with debouncing
        private void OnSteamToolsChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Debounce - wait 500ms after last change before processing
                _debounceTimer?.Dispose();
                _debounceTimer = new System.Threading.Timer(_ =>
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 📝 SteamTools.lua changed, updating DLC cache...{Environment.NewLine}");
                    Task.Run(() => ProcessDlcCacheInBackground());
                }, null, 500, System.Threading.Timeout.Infinite);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error handling SteamTools change: {ex.Message}{Environment.NewLine}");
            }
        }

        // Process DLC cache in background
        private async Task ProcessDlcCacheInBackground()
        {
            // Prevent multiple simultaneous processing
            if (_isProcessingDlcCache)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ⏭️ DLC cache processing already in progress, skipping{Environment.NewLine}");
                return;
            }

            _isProcessingDlcCache = true;

            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🔄 Starting background DLC cache update...{Environment.NewLine}");

                var installedDLCs = ReadInstalledDLCs();
                if (installedDLCs.Count == 0)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: No DLCs found in SteamTools.lua{Environment.NewLine}");

                    // Clean cache if steamtools is empty
                    var emptyCache = LoadDlcCacheFromDisk();
                    if (emptyCache.DlcData.Count > 0)
                    {
                        emptyCache.DlcData.Clear();
                        emptyCache.LastSteamToolsDlcs.Clear();
                        SaveDlcCacheToDisk(emptyCache);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🧹 Cleaned DLC cache{Environment.NewLine}");
                    }

                    _isProcessingDlcCache = false;
                    return;
                }

                var persistentCache = LoadDlcCacheFromDisk();
                var currentDlcSet = new HashSet<string>(installedDLCs);
                var lastDlcSet = new HashSet<string>(persistentCache.LastSteamToolsDlcs);

                var newDlcs = currentDlcSet.Except(lastDlcSet).ToList();
                var removedDlcs = lastDlcSet.Except(currentDlcSet).ToList();

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: 📊 Background DLC check: {newDlcs.Count} new, {removedDlcs.Count} removed{Environment.NewLine}");

                // Remove deleted DLCs from cache
                foreach (var removedDlc in removedDlcs)
                {
                    if (persistentCache.DlcData.ContainsKey(removedDlc))
                    {
                        persistentCache.DlcData.Remove(removedDlc);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🗑️ Removed {removedDlc} from background cache{Environment.NewLine}");
                    }
                }

                // Process new DLCs
                if (newDlcs.Count > 0)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🆕 Processing {newDlcs.Count} new DLCs in background...{Environment.NewLine}");

                    using (var httpClient = new System.Net.Http.HttpClient())
                    {
                        httpClient.Timeout = TimeSpan.FromSeconds(15);

                        var processedGames = new HashSet<string>();
                        var remainingDlcs = new HashSet<string>(newDlcs);

                        while (remainingDlcs.Count > 0)
                        {
                            string dlcId = remainingDlcs.First();

                            try
                            {
                                // Get parent game ID
                                string parentGameId = await GetParentAppIdFromSteam(dlcId, httpClient);

                                if (parentGameId == null)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ❌ Could not get parent game for DLC {dlcId}{Environment.NewLine}");
                                    remainingDlcs.Remove(dlcId);
                                    continue;
                                }

                                // Skip if already processed this game
                                if (processedGames.Contains(parentGameId))
                                {
                                    remainingDlcs.Remove(dlcId);
                                    continue;
                                }

                                processedGames.Add(parentGameId);

                                // Get game DLC info
                                var gameInfo = await GetGameDlcsFromSteam(parentGameId, httpClient);

                                if (gameInfo != null)
                                {
                                    // Find which new DLCs belong to this game
                                    var newDlcsForGame = gameInfo.AllDlcIds.Where(id => newDlcs.Contains(id)).ToList();

                                    foreach (var newDlcId in newDlcsForGame)
                                    {
                                        var dlcDetails = await GetDlcDetailsFromSteam(newDlcId, httpClient);

                                        persistentCache.DlcData[newDlcId] = new CachedDlcInfo
                                        {
                                            DlcId = newDlcId,
                                            GameId = parentGameId,
                                            GameName = gameInfo.GameName,
                                            DlcName = dlcDetails?.name ?? $"DLC {newDlcId}",
                                            HeaderImage = dlcDetails?.header_image,
                                            ReleaseDate = dlcDetails?.release_date,
                                            CachedAt = DateTime.Now
                                        };

                                        remainingDlcs.Remove(newDlcId);
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ✅ Cached DLC {newDlcId} ({dlcDetails?.name}){Environment.NewLine}");
                                    }
                                }
                                else
                                {
                                    remainingDlcs.Remove(dlcId);
                                }

                                await Task.Delay(200); // Be nice to Steam API
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing DLC {dlcId}: {ex.Message}{Environment.NewLine}");
                                remainingDlcs.Remove(dlcId);
                            }
                        }
                    }
                }

                // Update and save cache
                persistentCache.LastSteamToolsDlcs = installedDLCs.ToList();
                SaveDlcCacheToDisk(persistentCache);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ✅ Background DLC cache update complete{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in background DLC processing: {ex.Message}{Environment.NewLine}");
            }
            finally
            {
                _isProcessingDlcCache = false;
            }
        }

        // Method to get parent app ID from Steam API
        private async Task<string> GetParentAppIdFromSteam(string dlcId, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                string steamApiUrl = $"https://store.steampowered.com/api/appdetails?appids={dlcId}";
                var response = await httpClient.GetStringAsync(steamApiUrl);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                if (data[dlcId]?.success == true && data[dlcId]?.data != null)
                {
                    var appData = data[dlcId].data;

                    // Check if this is a DLC (has fullgame property)
                    if (appData.fullgame != null && appData.fullgame.appid != null)
                    {
                        return appData.fullgame.appid.ToString();
                    }

                    // If not a DLC, it's the game itself
                    return dlcId;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting parent app for {dlcId}: {ex.Message}{Environment.NewLine}");
            }

            return null;
        }

        // Method to get all DLCs for a game from Steam API
        private async Task<SteamGameDlcInfo> GetGameDlcsFromSteam(string gameId, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                // Check cache first (valid for 1 hour)
                lock (_cacheLock)
                {
                    if (_steamDlcCache.ContainsKey(gameId))
                    {
                        var cached = _steamDlcCache[gameId];
                        if ((DateTime.Now - cached.CachedAt).TotalHours < 1)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 💾 Using cached Steam data for game {gameId}{Environment.NewLine}");
                            return cached;
                        }
                    }
                }

                string steamApiUrl = $"https://store.steampowered.com/api/appdetails?appids={gameId}";
                var response = await httpClient.GetStringAsync(steamApiUrl);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                if (data[gameId]?.success == true && data[gameId]?.data != null)
                {
                    var appData = data[gameId].data;
                    var info = new SteamGameDlcInfo
                    {
                        GameId = gameId,
                        GameName = appData.name?.ToString() ?? $"Game {gameId}",
                        AllDlcIds = new List<string>(),
                        CachedAt = DateTime.Now
                    };

                    // Extract DLC IDs from the dlc array
                    if (appData.dlc != null)
                    {
                        foreach (var dlc in appData.dlc)
                        {
                            info.AllDlcIds.Add(dlc.ToString());
                        }
                    }

                    // Cache the result
                    lock (_cacheLock)
                    {
                        _steamDlcCache[gameId] = info;
                    }

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🎮 Steam API: Game {info.GameName} has {info.AllDlcIds.Count} DLCs{Environment.NewLine}");
                    return info;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting DLCs from Steam for game {gameId}: {ex.Message}{Environment.NewLine}");
            }

            return null;
        }

        // Method to get individual DLC details from Steam API
        private async Task<dynamic> GetDlcDetailsFromSteam(string dlcId, System.Net.Http.HttpClient httpClient)
        {
            try
            {
                string steamApiUrl = $"https://store.steampowered.com/api/appdetails?appids={dlcId}";
                var response = await httpClient.GetStringAsync(steamApiUrl);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                if (data[dlcId]?.success == true && data[dlcId]?.data != null)
                {
                    var dlcData = data[dlcId].data;
                    return new
                    {
                        name = dlcData.name?.ToString() ?? $"DLC {dlcId}",
                        header_image = dlcData.header_image?.ToString(),
                        release_date = dlcData.release_date
                    };
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting details for DLC {dlcId}: {ex.Message}{Environment.NewLine}");
            }

            return null;
        }

        // Method to get installed DLCs using SMART STEAM API - ONE REQUEST PER GAME VERSION
        public async Task<string> GetInstalledDLCsWithNamesAsync()
        {
            try
            {
                var installedDLCs = ReadInstalledDLCs();

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🚀 STARTING SMART STEAM API SEARCH: Found {installedDLCs.Count} DLCs{Environment.NewLine}");

                // Load persistent cache from disk
                var persistentCache = LoadDlcCacheFromDisk();

                // Detect changes in steamtools.lua
                var currentDlcSet = new HashSet<string>(installedDLCs);
                var lastDlcSet = new HashSet<string>(persistentCache.LastSteamToolsDlcs);

                var removedDlcs = lastDlcSet.Except(currentDlcSet).ToList();
                var newDlcs = currentDlcSet.Except(lastDlcSet).ToList();
                var existingDlcs = currentDlcSet.Intersect(lastDlcSet).ToList();

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: 📊 DLC Changes: {newDlcs.Count} new, {existingDlcs.Count} existing, {removedDlcs.Count} removed{Environment.NewLine}");

                // Remove deleted DLCs from cache
                foreach (var removedDlc in removedDlcs)
                {
                    if (persistentCache.DlcData.ContainsKey(removedDlc))
                    {
                        persistentCache.DlcData.Remove(removedDlc);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🗑️ Removed {removedDlc} from cache{Environment.NewLine}");
                    }
                }

                // If no DLCs after cleanup, update cache and return empty
                if (installedDLCs.Count == 0)
                {
                    persistentCache.LastSteamToolsDlcs = new List<string>();
                    SaveDlcCacheToDisk(persistentCache);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ✅ Cache cleaned, no DLCs in SteamTools.lua{Environment.NewLine}");
                    return "{\"games\": [], \"total_dlcs\": 0}";
                }

                var gameCards = new Dictionary<string, GameCard>();
                var processedDlcs = new HashSet<string>();
                var processedGames = new HashSet<string>();

                // First, populate with existing cached DLCs
                foreach (var existingDlc in existingDlcs)
                {
                    if (persistentCache.DlcData.ContainsKey(existingDlc))
                    {
                        var cached = persistentCache.DlcData[existingDlc];

                        if (!gameCards.ContainsKey(cached.GameId))
                        {
                            gameCards[cached.GameId] = new GameCard
                            {
                                game_id = cached.GameId,
                                game_name = cached.GameName,
                                dlc_count = 0,
                                dlc = new List<object>()
                            };
                        }

                        gameCards[cached.GameId].dlc.Add(new
                        {
                            id = cached.DlcId,
                            name = cached.DlcName,
                            installed = true,
                            capsule_image = cached.HeaderImage,
                            release_date = cached.ReleaseDate
                        });

                        gameCards[cached.GameId].dlc_count = gameCards[cached.GameId].dlc.Count;
                        processedDlcs.Add(existingDlc);
                        processedGames.Add(cached.GameId); // Mark game as processed
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 💾 Loaded {existingDlcs.Count} DLCs from cache{Environment.NewLine}");

                // Only process NEW DLCs via Steam API
                var remainingDlcs = new HashSet<string>(newDlcs);

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);

                    // SMART STEAM API APPROACH: One request per game, skip all DLCs from same game
                    while (remainingDlcs.Count > 0)
                    {
                        string dlcId = remainingDlcs.First();

                        try
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🔍 Checking DLC {dlcId} from Steam API (remaining: {remainingDlcs.Count}){Environment.NewLine}");

                            // Step 1: Get parent game ID for this DLC
                            string parentGameId = await GetParentAppIdFromSteam(dlcId, httpClient);

                            if (parentGameId == null)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ❌ Could not get parent game for DLC {dlcId}{Environment.NewLine}");
                                AddSingleDlcToUnknown(dlcId, gameCards, processedDlcs, persistentCache);
                                remainingDlcs.Remove(dlcId);
                                continue;
                            }

                            // Step 2: Skip if we already processed this game
                            if (processedGames.Contains(parentGameId))
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ⏭️ Game {parentGameId} already processed, skipping DLC {dlcId}{Environment.NewLine}");
                                remainingDlcs.Remove(dlcId);
                                continue;
                            }

                            processedGames.Add(parentGameId);

                            // Step 3: Get ALL DLCs for this game from Steam
                            var gameInfo = await GetGameDlcsFromSteam(parentGameId, httpClient);

                            if (gameInfo == null)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ❌ Could not get DLC info for game {parentGameId}{Environment.NewLine}");
                                AddSingleDlcToUnknown(dlcId, gameCards, processedDlcs, persistentCache);
                                remainingDlcs.Remove(dlcId);
                                continue;
                            }

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🎮 Processing game: {gameInfo.GameName} (ID: {parentGameId}){Environment.NewLine}");

                            // Step 4: Find which DLCs from this game are installed
                            var installedDlcsForThisGame = gameInfo.AllDlcIds.Where(id => installedDLCs.Contains(id)).ToList();

                            if (installedDlcsForThisGame.Count > 0)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ✅ Found {installedDlcsForThisGame.Count} installed DLCs for {gameInfo.GameName}: [{string.Join(", ", installedDlcsForThisGame)}]{Environment.NewLine}");

                                // Create game card
                                if (!gameCards.ContainsKey(parentGameId))
                                {
                                    gameCards[parentGameId] = new GameCard
                                    {
                                        game_id = parentGameId,
                                        game_name = gameInfo.GameName,
                                        dlc_count = 0,
                                        dlc = new List<object>()
                                    };
                                }

                                // Add ALL installed DLCs for this game
                                foreach (string installedDlcId in installedDlcsForThisGame)
                                {
                                    // Get DLC details from Steam API
                                    var dlcDetails = await GetDlcDetailsFromSteam(installedDlcId, httpClient);
                                    string dlcName = dlcDetails?.name ?? $"DLC {installedDlcId}";

                                    gameCards[parentGameId].dlc.Add(new
                                    {
                                        id = installedDlcId,
                                        name = dlcName,
                                        installed = true,
                                        capsule_image = dlcDetails?.header_image,
                                        release_date = dlcDetails?.release_date
                                    });

                                    processedDlcs.Add(installedDlcId);

                                    // Save new DLC to persistent cache
                                    persistentCache.DlcData[installedDlcId] = new CachedDlcInfo
                                    {
                                        DlcId = installedDlcId,
                                        GameId = parentGameId,
                                        GameName = gameInfo.GameName,
                                        DlcName = dlcName,
                                        HeaderImage = dlcDetails?.header_image,
                                        ReleaseDate = dlcDetails?.release_date,
                                        CachedAt = DateTime.Now
                                    };
                                }

                                gameCards[parentGameId].dlc_count = gameCards[parentGameId].dlc.Count;

                                // Remove ALL processed DLCs from remaining list (this is the key optimization!)
                                foreach (string installedDlcId in installedDlcsForThisGame)
                                {
                                    remainingDlcs.Remove(installedDlcId);
                                }

                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🗑️ Removed {installedDlcsForThisGame.Count} DLCs from queue. Remaining: {remainingDlcs.Count}{Environment.NewLine}");
                            }
                            else
                            {
                                // No installed DLCs for this game, just remove the current one
                                remainingDlcs.Remove(dlcId);
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam API failed for {dlcId}: {ex.Message}{Environment.NewLine}");
                            AddSingleDlcToUnknown(dlcId, gameCards, processedDlcs, persistentCache);
                            remainingDlcs.Remove(dlcId);
                        }

                        // Small delay to be nice to Steam API
                        await Task.Delay(200);
                    }
                }

                // Update cache with current steamtools.lua state and save to disk
                persistentCache.LastSteamToolsDlcs = installedDLCs.ToList();
                SaveDlcCacheToDisk(persistentCache);

                // Build final result
                var result = new
                {
                    games = gameCards.Values.Select(card => new
                    {
                        game_id = card.game_id,
                        game_name = card.game_name,
                        dlc_count = card.dlc_count,
                        dlc = card.dlc
                    }).ToList(),
                    total_dlcs = installedDLCs.Count
                };

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: 🎯 SMART STEAM API RESULT: {gameCards.Count} games, {processedDlcs.Count} DLCs processed, {processedGames.Count} unique games checked, {newDlcs.Count} new DLCs added to cache{Environment.NewLine}");
                return Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in GetInstalledDLCsWithNamesAsync: {ex.Message}{Environment.NewLine}");
                return "{\"error\": \"Failed to process installed DLCs\", \"games\": [], \"total_dlcs\": 0}";
            }
        }

        // Helper method to chunk lists (since .NET Framework doesn't have Chunk)
        private static IEnumerable<List<T>> ChunkList<T>(List<T> source, int chunkSize)
        {
            for (int i = 0; i < source.Count; i += chunkSize)
            {
                yield return source.GetRange(i, Math.Min(chunkSize, source.Count - i));
            }
        }

        private void ProcessCachedDlc(string dlcId, dynamic cachedData, Dictionary<string, GameCard> gameCards, HashSet<string> processedDlcs)
        {
            try
            {
                string gameId = cachedData.gameId?.ToString() ?? dlcId;
                string gameName = cachedData.gameName?.ToString() ?? $"Game {gameId}";
                string dlcName = cachedData.name?.ToString() ?? $"DLC {dlcId}";

                if (!gameCards.ContainsKey(gameId))
                {
                    gameCards[gameId] = new GameCard
                    {
                        game_id = gameId,
                        game_name = gameName,
                        dlc_count = 0,
                        dlc = new List<object>()
                    };
                }

                gameCards[gameId].dlc.Add(new
                {
                    id = dlcId,
                    name = dlcName,
                    installed = true,
                    capsule_image = cachedData.capsule_image?.ToString(),
                    release_date = cachedData.release_date
                });
                gameCards[gameId].dlc_count = gameCards[gameId].dlc.Count;
                processedDlcs.Add(dlcId);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing cached DLC {dlcId}: {ex.Message}{Environment.NewLine}");
            }
        }

        private void AddSingleDlcToUnknown(string dlcId, Dictionary<string, GameCard> gameCards, HashSet<string> processedDlcs, PersistentDlcCache persistentCache = null)
        {
            if (!gameCards.ContainsKey("unknown"))
            {
                gameCards["unknown"] = new GameCard
                {
                    game_id = "unknown",
                    game_name = "Unknown Games",
                    dlc_count = 0,
                    dlc = new List<object>()
                };
            }

            gameCards["unknown"].dlc.Add(new
            {
                id = dlcId,
                name = $"Unknown DLC {dlcId}",
                installed = true
            });
            gameCards["unknown"].dlc_count = gameCards["unknown"].dlc.Count;
            processedDlcs.Add(dlcId);

            // Save to persistent cache if provided
            if (persistentCache != null)
            {
                persistentCache.DlcData[dlcId] = new CachedDlcInfo
                {
                    DlcId = dlcId,
                    GameId = "unknown",
                    GameName = "Unknown Games",
                    DlcName = $"Unknown DLC {dlcId}",
                    HeaderImage = null,
                    ReleaseDate = null,
                    CachedAt = DateTime.Now
                };
            }
        }

        private void AddToUnknownDlcs(Dictionary<string, GameCard> gameCards, string dlcId)
        {
            if (!gameCards.ContainsKey("unknown"))
            {
                gameCards["unknown"] = new GameCard
                {
                    game_id = "unknown",
                    game_name = "Unknown DLCs",
                    dlc_count = 0,
                    dlc = new List<object>()
                };
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: 🎮 Created Unknown DLCs card{Environment.NewLine}");
            }

            gameCards["unknown"].dlc.Add(new
            {
                id = dlcId,
                name = $"Unknown DLC {dlcId}",
                installed = true
            });
            gameCards["unknown"].dlc_count = gameCards["unknown"].dlc.Count;
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: ❓ Added {dlcId} to Unknown DLCs{Environment.NewLine}");
        }

        public async Task<string> FetchDLCDataAsync(string gameId)
        {
            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    string apiUrl = $"https://apiurl/api/v3/fetchdlc/{gameId}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fetching DLC data from {apiUrl}{Environment.NewLine}");

                    var response = await httpClient.GetStringAsync(apiUrl);
                    var apiData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);

                    bool isComplete = apiData.is_complete != null ? (bool)apiData.is_complete : true;

                    if (!isComplete)
                    {

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: API still processing DLC data for game {gameId}{Environment.NewLine}");
                        return response;
                    }

                    var installedDLCs = ReadInstalledDLCs();

                    if (apiData.dlc != null)
                    {
                        foreach (var dlcEntry in apiData.dlc)
                        {
                            string dlcId = dlcEntry.Name;
                            var dlcInfo = dlcEntry.Value;

                            dlcInfo.installed = installedDLCs.Contains(dlcId);
                        }
                    }

                    apiData.installed_count = installedDLCs.Count;

                    string modifiedResponse = Newtonsoft.Json.JsonConvert.SerializeObject(apiData);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: DLC data fetched and enhanced with install status{Environment.NewLine}");

                    return modifiedResponse;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error fetching DLC data: {ex.Message}{Environment.NewLine}");
                return null;
            }
        }
    }
}
