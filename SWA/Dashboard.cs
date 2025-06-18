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

namespace SWA
{
    public partial class Dashboard : Form
    {
        private WebView2 webView;
        private string apiUrl;
        private HeartbeatSystem heartbeatSystem;
        private FileSystemWatcher userDataWatcher;
        private System.Threading.Timer userDataCheckTimer;
        private FormWindowState previousWindowState;

        private Dictionary<string, DateTime> gameInfoRequests = new Dictionary<string, DateTime>();
        private readonly object gameInfoLock = new object();

        public const string APP_VERSION = "SWA v1.2";

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

            if (m.Msg == WM_NCCALCSIZE && m.WParam.ToInt32() == 1)
            {

                ApplyRoundedCorners();
            }
        }

        private void ApplyRoundedCorners()
        {

            IntPtr region = CreateRoundRectRgn(0, 0, this.Width, this.Height, 20, 20);

            SetWindowRgn(this.Handle, region, true);
        }

        public Dashboard()
        {
            InitializeComponent();

            this.Activated += Dashboard_Activated;
            this.Deactivate += Dashboard_Deactivate;

            InitializeWebViewAsync().ConfigureAwait(false);

            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "SWA V2 - Dashboard";

            this.Load += (s, e) => ApplyRoundedCorners();

            SetupUserDataMonitoring();

            this.FormClosing += Dashboard_FormClosing;

            this.Load += async (s, e) => await DownloadAndPatchSteamAsync();

            StartRestrictionCheckTimer();

            this.Resize += Dashboard_Resize;
            this.previousWindowState = this.WindowState;
        }

        private bool isReloadingDashboard = false;

        private CancellationTokenSource reloadCancellationTokenSource = null;

        private void Dashboard_Resize(object sender, EventArgs e)
        {
            try
            {

                ApplyRoundedCorners();

                if (this.WindowState != FormWindowState.Minimized && this.previousWindowState == FormWindowState.Minimized)
                {

                    this.WindowState = FormWindowState.Maximized;

                    if (isReloadingDashboard)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Dashboard reload already in progress, skipping additional reload{Environment.NewLine}");
                        return;
                    }

                    if (reloadCancellationTokenSource != null)
                    {
                        reloadCancellationTokenSource.Cancel();
                        reloadCancellationTokenSource.Dispose();
                    }

                    reloadCancellationTokenSource = new CancellationTokenSource();
                    var token = reloadCancellationTokenSource.Token;

                    this.BeginInvoke(new Action(() => {
                        try
                        {

                            isReloadingDashboard = true;

                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Полное пересоздание WebView после восстановления из свернутого состояния{Environment.NewLine}");

                            string lastUrl = null;
                            try
                            {
                                if (webView?.CoreWebView2 != null)
                                {
                                    lastUrl = webView.CoreWebView2.Source;
                                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                                        $"{DateTime.Now}: Сохранен текущий URL: {lastUrl}{Environment.NewLine}");
                                }
                            }
                            catch (Exception urlEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Ошибка при получении текущего URL: {urlEx.Message}{Environment.NewLine}");
                            }

                            if (webView?.CoreWebView2 != null)
                            {
                                try
                                {
                                    webView.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                                    webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                                }
                                catch (Exception eventEx)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                                        $"{DateTime.Now}: Ошибка при отписке от событий: {eventEx.Message}{Environment.NewLine}");
                                }
                            }

                            try
                            {
                                if (webView != null)
                                {
                                    this.Controls.Remove(webView);
                                    webView.Dispose();
                                    webView = null;
                                }
                            }
                            catch (Exception disposeEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Ошибка при удалении WebView: {disposeEx.Message}{Environment.NewLine}");
                            }

                            System.Threading.Thread.Sleep(50);

                            if (token.IsCancellationRequested)
                                return;

                            webView = new WebView2();
                            webView.Dock = DockStyle.Fill;
                            webView.AllowExternalDrop = false;
                            this.Controls.Add(webView);

                            InitWebViewAfterRestore(lastUrl);

                            System.Threading.Thread.Sleep(100);

                            FastRefreshWebView();

                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: WebView успешно пересоздан после восстановления из свернутого состояния{Environment.NewLine}");
                        }
                        catch (TaskCanceledException)
                        {

                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Пересоздание WebView было отменено{Environment.NewLine}");
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Ошибка при пересоздании WebView: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                            try
                            {

                                this.Controls.Clear();

                                webView = new WebView2();
                                webView.Dock = DockStyle.Fill;
                                webView.AllowExternalDrop = false;
                                this.Controls.Add(webView);

                                InitializeWebViewAsync();
                            }
                            catch (Exception fallbackEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Критическая ошибка при повторной инициализации WebView: {fallbackEx.Message}{Environment.NewLine}{fallbackEx.StackTrace}{Environment.NewLine}");
                            }
                        }
                        finally
                        {

                            isReloadingDashboard = false;
                        }
                    }));
                }

                if (this.WindowState != this.previousWindowState)
                {
                    this.previousWindowState = this.WindowState;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in Dashboard_Resize: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
        }

        private async void InitWebViewAfterRestore(string lastUrl)
        {
            try
            {

                if (!this.InvokeRequired)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Ускоренная инициализация WebView после восстановления. LastUrl: {lastUrl ?? "null"} (UI поток){Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Запрос на ускоренную инициализацию WebView из фонового потока, переключаемся на UI поток. LastUrl: {lastUrl ?? "null"}{Environment.NewLine}");

                    this.Invoke(new Action(() =>
                    {
                        InitWebViewAfterRestore(lastUrl);
                    }));
                    return;
                }

                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");

                if (!Directory.Exists(userDataFolder))
                {
                    Directory.CreateDirectory(userDataFolder);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Создана папка для данных WebView: {userDataFolder}{Environment.NewLine}");
                }

                var options = new CoreWebView2EnvironmentOptions();
                options.AdditionalBrowserArguments = "--disable-web-security --disable-features=IsolateOrigins,site-per-process";

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                await webView.EnsureCoreWebView2Async(env);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WebView2 окружение создано успешно{Environment.NewLine}");

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.ZoomFactor = 1.0;

                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    try {
                        caches.keys().then(function(names) {
                            for (let name of names) caches.delete(name);
                        });
                        console.log('Browser cache cleared');
                    } catch (error) {
                        console.error('Error clearing cache:', error);
                    }
                ");

                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.NavigationCompleted += WebView_NavigationCompleted;

                webView.ZoomFactorChanged += (sender, evt) => {
                    if (webView.ZoomFactor != 1.0)
                    {
                        webView.ZoomFactor = 1.0;
                    }
                };

                webView.CoreWebView2.ProcessFailed += (sender, e) => {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Ошибка процесса WebView: {e.ProcessFailedKind}, Код: {e.ExitCode}{Environment.NewLine}");
                };

                if (!string.IsNullOrEmpty(lastUrl))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Восстановление последнего URL: {lastUrl}{Environment.NewLine}");
                    webView.CoreWebView2.Navigate(lastUrl);
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Загрузка дашборда по умолчанию{Environment.NewLine}");
                    await LoadDashboardAsync();
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: WebView recreation completed successfully{Environment.NewLine}");

                webView.Focus();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in InitWebViewAfterRestore: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Attempting full WebView initialization after error{Environment.NewLine}");
                    await InitializeWebViewAsync();
                }
                catch (Exception fallbackEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Critical error in fallback initialization: {fallbackEx.Message}{Environment.NewLine}{fallbackEx.StackTrace}{Environment.NewLine}");
                }
            }
        }

        private System.Threading.Timer restrictionCheckTimer;

        private void StartRestrictionCheckTimer()
        {
            try
            {

                restrictionCheckTimer = new System.Threading.Timer(
                    CheckRestrictionTimerCallback,
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1)
                );

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Started periodic restriction checking timer (1-minute interval){Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error starting restriction check timer: {ex.Message}{Environment.NewLine}");
            }
        }

        private void CheckRestrictionTimerCallback(object state)
        {
            try
            {

                this.BeginInvoke(new Action(() =>
                {

                    CheckRestrictionTimerCallbackAsync();
                }));
            }
            catch (Exception ex)
            {

                if (!this.IsDisposed && !this.Disposing)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error in restriction timer callback: {ex.Message}{Environment.NewLine}");
                }
            }
        }

        private async void CheckRestrictionTimerCallbackAsync()
        {
            try
            {

                if (await CheckForMaintenanceMode())
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Server has entered maintenance mode during the user's session. Redirecting to maintenance page.{Environment.NewLine}");

                    LoadMaintenancePage();

                    StopRestrictionCheckTimer();
                }

                else if (await CheckForUserRestrictions())
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: User has become restricted during their session. Redirecting to restriction page.{Environment.NewLine}");

                    LoadRestrictionPage();

                    StopRestrictionCheckTimer();
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in restriction check timer callback: {ex.Message}{Environment.NewLine}");
            }
        }

        private void CheckUserDataFile(object state)
        {
            try
            {

                this.BeginInvoke(new Action(() =>
                {

                    RefreshUserDataFromFileAsync();
                }));
            }
            catch (Exception ex)
            {

                if (!this.IsDisposed && !this.Disposing)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error in timer callback: {ex.Message}{Environment.NewLine}");
                }
            }
        }

        private async void RefreshUserDataFromFileAsync()
        {
            try
            {
                await RefreshUserDataFromFile();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in periodic file check: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task RefreshUserDataFromFile()
        {
            try
            {
                string userDataPath = @"C:\GFK\user_data.json";
                if (File.Exists(userDataPath) && this.webView?.CoreWebView2 != null)
                {
                    string json = File.ReadAllText(userDataPath);
                    dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

                    string premiumDisplay = "Standard";
                    try
                    {
                        if (userData.premium_expires_in_days != null)
                        {
                            int premiumDays = Convert.ToInt32(userData.premium_expires_in_days);
                            if (premiumDays > 9000)
                            {
                                premiumDisplay = "∞ Lifetime";
                            }
                            else if (premiumDays > 0)
                            {
                                premiumDisplay = $"{premiumDays} days left";
                            }
                        }
                        else if (userData.status != null && userData.status.ToString().Contains("Premium"))
                        {
                            premiumDisplay = userData.status;
                        }
                    }
                    catch (Exception exPremium)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Error processing premium days: {exPremium.Message}{Environment.NewLine}");
                    }

                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            const username = document.querySelector('.username');
                            if (username) username.textContent = '{userData.username ?? "User"}';

                            const userPlan = document.querySelector('.user-plan');
                            if (userPlan) userPlan.textContent = '{premiumDisplay}';

                            if (typeof updatePlanBadge === 'function') {{
                                updatePlanBadge('{userData.status ?? "Standard"}');
                            }}

                            console.log('User data refreshed from file');
                        }} catch (error) {{
                            console.error('Error refreshing user data:', error);
                        }}
                    ");

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Updated UI with refreshed user data, premium: {premiumDisplay}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error refreshing user data: {ex.Message}{Environment.NewLine}");
            }
        }

        private void Dashboard_FormClosing(object sender, FormClosingEventArgs e)
        {

            try
            {

                if (reloadCancellationTokenSource != null)
                {
                    reloadCancellationTokenSource.Cancel();
                    reloadCancellationTokenSource.Dispose();
                    reloadCancellationTokenSource = null;
                }

                heartbeatSystem?.Stop();

                if (userDataWatcher != null)
                {
                    userDataWatcher.EnableRaisingEvents = false;
                    userDataWatcher.Dispose();
                }

                userDataCheckTimer?.Dispose();
                StopRestrictionCheckTimer();
                StopMaintenanceStatusCheck();

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

                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

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

                if (await CheckForMaintenanceMode())
                {

                    LoadMaintenancePage();
                }

                else if (await CheckForUserRestrictions())
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

                using (var client = new System.Net.Http.HttpClient())
                {

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

                    var response = await client.PostAsync(restrictionUrl, content);

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
                            else if (result.maintenance == true)
                            {
                                restrictionReason = result.maintenance_message ?? "Server is currently under maintenance.";
                                restrictionRedirectUrl = result.maintenance_url ?? "";
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Maintenance mode active: {restrictionReason}{Environment.NewLine}");
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
                string dashboardUrl;

                if (ApiConfigManager.Config.Ui.Local == 1)
                {

                    dashboardUrl = Path.Combine(Application.StartupPath, "UI", "dashboard.html");
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using local dashboard path: {dashboardUrl}{Environment.NewLine}");
                }
                else
                {

                    dashboardUrl = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.Ui.Paths.Dashboard}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using remote dashboard path: {dashboardUrl}{Environment.NewLine}");
                }

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

                webView.CoreWebView2.Navigate(new Uri(dashboardUrl).ToString());

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Navigating to dashboard URL: {dashboardUrl}{Environment.NewLine}");
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

                try
                {

                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(@"
                            try {

                                const overlay = document.getElementById('loading-overlay');
                                if (overlay) {
                                    overlay.classList.add('hidden');
                                    overlay.style.display = 'none';
                                    console.log('Loading screen hidden immediately after navigation');
                                }
                            } catch (error) {
                                console.error('Error hiding loading screen:', error);
                            }
                        ");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Невозможно скрыть экран загрузки: webView или CoreWebView2 равны null{Environment.NewLine}");
                    }
                }
                catch (Exception hideEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error hiding loading screen: {hideEx.Message}{Environment.NewLine}");
                }

                if (e.IsSuccess)
                {

                    isReloadingDashboard = false;

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
                        }} catch (error) {{                        
                            console.error('Error initializing version:', error);                    
                        }}                
                    ");

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {
                            console.log('Dashboard initialized successfully');
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
                    await CheckForUpdates(false);

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

                    await Task.Delay(300);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Completing loading and hiding loading screen{Environment.NewLine}");

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {

                            if (typeof completeLoading === 'function') {
                                completeLoading();
                                console.log('Loading screen hidden by explicit call to completeLoading()');
                            } else {
                                console.warn('completeLoading function not found');
                            }

                            const overlay = document.getElementById('loading-overlay');
                            if (overlay) {
                                overlay.classList.add('hidden');
                                overlay.style.display = 'none';
                                console.log('Loading screen hidden directly');
                            }

                            window.loadingScreenTimeout = setTimeout(() => {
                                console.log('Executing 10-second safety timeout for loading screen');
                                const loadingOverlay = document.getElementById('loading-overlay');
                                if (loadingOverlay) {
                                    loadingOverlay.classList.add('hidden');
                                    loadingOverlay.style.display = 'none';
                                    console.log('Loading screen hidden by safety timeout');
                                }

                                document.querySelectorAll('.loading-screen, .loader, .spinner').forEach(el => {
                                    el.style.display = 'none';
                                    console.log('Additional loading element hidden');
                                });
                            }, 10000);
                        } catch (error) {
                            console.error('Error completing loading:', error);
                        }
                    ");
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

                    string premiumDisplay = "Standard";
                    try
                    {
                        if (userData.premium_expires_in_days != null)
                        {
                            int premiumDays = Convert.ToInt32(userData.premium_expires_in_days);
                            if (premiumDays > 9000)
                            {
                                premiumDisplay = "∞ Lifetime";
                            }
                            else if (premiumDays > 0)
                            {
                                premiumDisplay = $"{premiumDays} days left";
                            }
                        }
                        else if (userData.status != null && userData.status.ToString().Contains("Premium"))
                        {
                            premiumDisplay = userData.status;
                        }
                    }
                    catch (Exception exPremium)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing premium days: {exPremium.Message}{Environment.NewLine}");
                    }

                    try
                    {

                        string username = userData?.username ?? "User";
                        await UpdateLogMessage($"Welcome back, {username}!");

                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                console.log('Loading user data...');

                                const username = document.querySelector('.username');
                                if (username) username.textContent = '{userData.username ?? "User"}';

                                const userPlan = document.querySelector('.user-plan');
                                if (userPlan) userPlan.textContent = '{premiumDisplay}';

                                setTimeout(() => {{
                                    if (typeof completeLoading === 'function') {{
                                        completeLoading();
                                        console.log('Loading screen hidden from user data load');
                                    }}
                                }}, 300);

                                if (typeof updatePlanBadge === 'function') {{
                                    updatePlanBadge('{userData.status ?? "Standard"}');
                                }}

                                if (typeof processServerResponse === 'function') {{
                                    const userData = {json};
                                    userData.premiumDisplay = '{premiumDisplay}';
                                    processServerResponse(userData);
                                }}

                                console.log('User data loaded successfully');

                                setTimeout(function() {{
                                    if (typeof completeLoading === 'function') {{
                                        completeLoading();
                                        console.log('Loading screen hidden');
                                    }}
                                }}, 500);
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
                catch { }
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

                File.WriteAllText(@"C:\GFK\user_data.json", jsonData);

                try
                {
                    var responseData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(jsonData);

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

                string deviceIdPath = @"C:\GFK\device_id.txt";
                if (File.Exists(deviceIdPath))
                {
                    return File.ReadAllText(deviceIdPath).Trim();
                }

                var cpuId = GetCpuId();
                var diskId = GetDiskId();

                string combinedId = $"{cpuId}-{diskId}";
                string hwid = BitConverter.ToString(
                    System.Security.Cryptography.MD5.Create().ComputeHash(
                        System.Text.Encoding.UTF8.GetBytes(combinedId)
                    )
                ).Replace("-", "");

                Directory.CreateDirectory(@"C:\GFK");
                File.WriteAllText(deviceIdPath, hwid);

                return hwid;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error getting device ID: {ex.Message}{Environment.NewLine}");
                return "HW-" + Guid.NewGuid().ToString().Substring(0, 8);
            }
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

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson.Trim('"');

            File.AppendAllText(@"C:\GFK\dashboard_message_log.txt", $"{DateTime.Now}: Message received: {message}{Environment.NewLine}");

            if (message.Contains("Server response:"))
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
            else if (message == "getPatchNotes")
            {

                GetPatchNotesAsync().ConfigureAwait(false);
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
                string gameId = message.Substring("addGame:".Length);
                TryAddGameAsync(gameId, false);
            }
            else if (message.StartsWith("updateGame:"))
            {

                string gameId = message.Substring("updateGame:".Length);
                TryAddGameAsync(gameId, true);
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
            else if (message == "restartSteam")
            {
                try
                {

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM steam.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    System.Threading.Thread.Sleep(2000);

                    string steamPath = GetSteamPathFromRegistry();
                    if (steamPath != "Not found")
                    {
                        string steamExe = System.IO.Path.Combine(steamPath, "steam.exe");
                        if (File.Exists(steamExe))
                        {
                            System.Diagnostics.Process.Start(steamExe);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam restarted by user from dashboard.\n");
                        }
                        else
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: steam.exe not found at {steamExe}\n");
                        }
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot restart.\n");
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

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM steam.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM SWAV2.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();

                    System.Threading.Thread.Sleep(2000);

                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string swaExe = Path.Combine(appDir, "SWAV2.EXE");
                    bool started = false;
                    if (File.Exists(swaExe))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = swaExe,
                            WorkingDirectory = appDir,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWAV2.EXE restarted from app folder: {swaExe}\n");
                        started = true;
                    }
                    else
                    {

                        string steamPath = GetSteamPathFromRegistry();
                        if (steamPath != "Not found")
                        {
                            string steamSwaExe = Path.Combine(steamPath, "SWAV2.EXE");
                            if (File.Exists(steamSwaExe))
                            {
                                var psi = new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = steamSwaExe,
                                    WorkingDirectory = steamPath,
                                    UseShellExecute = true
                                };
                                System.Diagnostics.Process.Start(psi);
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWAV2.EXE restarted from Steam folder: {steamSwaExe}\n");
                                started = true;
                            }
                            else
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWAV2.EXE not found at {steamSwaExe}\n");
                            }
                        }
                    }
                    if (!started)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWAV2.EXE not found in app or Steam folder.\n");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error restarting SWAV2.EXE: {ex.Message}\n{ex.StackTrace}\n");
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

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path detected: {steamPath}{Environment.NewLine}");

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
                    statusMessage = "Detected (hid.dll and plugin folder present)";

                    if (pluginFolderExists)
                    {
                        ScanPluginGames(pluginFolderPath);
                    }
                }
                else if (hidDllExists)
                {
                    statusMessage = "Partial (only hid.dll found)";
                }
                else if (pluginFolderExists)
                {
                    statusMessage = "Partial (only plugin folder found)";

                    ScanPluginGames(pluginFolderPath);
                }
                else
                {
                    statusMessage = "Not detected";
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
                string[] luadFiles = Directory.GetFiles(pluginFolderPath, "*.luad");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Raw .lua files: {string.Join(", ", luaFiles)}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Raw .luad files: {string.Join(", ", luadFiles)}{Environment.NewLine}");

                Dictionary<string, string> allGameFiles = new Dictionary<string, string>();

                HashSet<string> allIds = new HashSet<string>();
                foreach (string file in luaFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.All(char.IsDigit))
                        allIds.Add(fileName);
                }
                foreach (string file in luadFiles)
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
                    bool hasLuad = File.Exists(Path.Combine(pluginFolderPath, gameId + ".luad"));
                    string fileType = hasLua ? "lua" : (hasLuad ? "luad" : "none");
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
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All .lua/.luad files: {string.Join(", ", debugList)}{Environment.NewLine}");

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

                string cleanGameId = gameId.Replace(".lua", "").Replace(".luad", "");

                string luaFileName = $"{cleanGameId}.lua";
                string luadFileName = $"{cleanGameId}.luad";
                string luaFilePath = Path.Combine(pluginFolderPath, luaFileName);
                string luadFilePath = Path.Combine(pluginFolderPath, luadFileName);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Toggle requested for game {cleanGameId}. Checking files: lua={File.Exists(luaFilePath)}, luad={File.Exists(luadFilePath)}, enable={enable}{Environment.NewLine}");

                try
                {
                    if (enable && File.Exists(luadFilePath))
                    {

                        if (File.Exists(luaFilePath))
                        {

                            File.Delete(luaFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted existing {luaFilePath}{Environment.NewLine}");
                        }

                        File.Move(luadFilePath, luaFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} enabled (renamed from .luad to .lua){Environment.NewLine}");
                    }
                    else if (!enable && File.Exists(luaFilePath))
                    {

                        if (File.Exists(luadFilePath))
                        {

                            File.Delete(luadFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted existing {luadFilePath}{Environment.NewLine}");
                        }

                        File.Move(luaFilePath, luadFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} disabled (renamed from .lua to .luad){Environment.NewLine}");
                    }
                    else if (enable && !File.Exists(luadFilePath) && !File.Exists(luaFilePath))
                    {

                        File.WriteAllText(luaFilePath, string.Empty);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} created as enabled (.lua){Environment.NewLine}");
                    }
                    else if (!enable && !File.Exists(luadFilePath) && !File.Exists(luaFilePath))
                    {

                        File.WriteAllText(luadFilePath, string.Empty);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} created as disabled (.luad){Environment.NewLine}");
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Could not toggle game {cleanGameId}. Unexpected file state: lua={File.Exists(luaFilePath)}, luad={File.Exists(luadFilePath)}, enable={enable}{Environment.NewLine}");
                    }
                }
                catch (IOException fileEx)
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: File operation error for game {cleanGameId}: {fileEx.Message}{Environment.NewLine}");

                    try
                    {
                        if (enable && File.Exists(luadFilePath))
                        {

                            File.WriteAllText(luaFilePath, string.Empty);

                            File.Delete(luadFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} enabled (alternative method){Environment.NewLine}");
                        }
                        else if (!enable && File.Exists(luaFilePath))
                        {

                            File.WriteAllText(luadFilePath, string.Empty);

                            File.Delete(luaFilePath);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {cleanGameId} disabled (alternative method){Environment.NewLine}");
                        }
                    }
                    catch (Exception alternativeEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Alternative method failed: {alternativeEx.Message}{Environment.NewLine}");
                    }
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

        private async Task TryAddGameAsync(string gameId, bool isUpdate)
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
                        return;
                    }

                    if (gameInfo != null && gameInfo.name != null && gameInfo.File == "0")
                    {
                        string gameName = gameInfo.name.ToString();
                        await UpdateLogMessage($"Game {gameName} is not available yet", "warning");
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} not available yet (File: 0){Environment.NewLine}");
                        return;
                    }

                    if (gameInfo != null && gameInfo.File == "1")
                    {

                        string gameName = gameInfo.name?.ToString() ?? gameId;
                        await UpdateLogMessage($"Found game: {gameName}", "success");

                        string access = gameInfo.access?.ToString() ?? "0";
                        if (access == "2" && isGuest)
                        {
                            await UpdateLogMessage($"Game {gameName} requires premium access.", "warning");
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} requires premium access, user is guest{Environment.NewLine}");
                            return;
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
                        }

                        string steamPath = GetSteamPathFromRegistry();
                        if (steamPath == "Not found" || !Directory.Exists(steamPath))
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot download game file{Environment.NewLine}");
                            await UpdateLogMessage("Error: Steam path not found", "error");
                            return;
                        }

                        string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                        if (!Directory.Exists(pluginFolderPath))
                        {
                            Directory.CreateDirectory(pluginFolderPath);
                        }

                        string fileUrl = $"{ApiConfigManager.Config.Api.TrimEnd('/')}/api/v3/file/{gameId}.zip";
                        string tempZipPath = Path.Combine(pluginFolderPath, $"{gameId}.zip");
                        string luaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.lua");

                        await UpdateLogMessage($"Downloading {gameName}...");

                        try
                        {
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

                                        await UpdateLogMessage($"Extracting {gameName}...");

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

                                        await UpdateLogMessage($"{gameName} successfully added!", "success");
                                    }
                                    else
                                    {
                                        string resp = await response.Content.ReadAsStringAsync();
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to download {fileUrl}: {response.StatusCode} {resp}{Environment.NewLine}");

                                        if (resp.Contains("username") && resp.Contains("required") ||
                                            resp.Contains("hardware") && resp.Contains("required") ||
                                            resp.Contains("authentication") && resp.Contains("required") ||
                                            resp.Contains("premium"))
                                        {
                                            if (isGuest)
                                            {
                                                await UpdateLogMessage($"Premium account required to download {gameName}. Please log in with a premium account.", "error");
                                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Premium required for game {gameId}, user is guest{Environment.NewLine}");
                                            }
                                            else
                                            {
                                                await UpdateLogMessage($"Failed to download {gameName}: Authentication required", "error");
                                            }
                                        }
                                        else
                                        {
                                            await UpdateLogMessage($"Failed to download {gameName}: {response.StatusCode}", "error");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error downloading/extracting game file: {ex.Message}{Environment.NewLine}");
                            await UpdateLogMessage($"Error downloading {gameName}: {ex.Message}", "error");
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
                                }
                                else
                                {
                                    await UpdateLogMessage($"Game {gameName} requires premium access", "warning");
                                }
                            }
                            else
                            {
                                await UpdateLogMessage($"Game {gameName} is unavailable", "warning");
                            }
                        }
                        else
                        {
                            await UpdateLogMessage($"Game {gameId} not found", "error");
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} not available for download (File: {gameInfo?.File ?? "null"}, Access: {gameInfo?.access ?? "null"}){Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Ошибка при добавлении игры {gameId}: {ex.Message}{Environment.NewLine}");
                await UpdateLogMessage($"Error searching for game {gameId}: {ex.Message}", "error");
            }
        }

        private async Task RemoveGameAndManifest(string gameId)
        {
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: RemoveGameAndManifest CALLED with gameId={gameId}{Environment.NewLine}");
            try
            {
                await UpdateLogMessage($"Removing game {gameId}...");

                string steamPath = GetSteamPathFromRegistry();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path not found, cannot remove game files{Environment.NewLine}");
                    await UpdateLogMessage("Error: Steam path not found", "error");
                    return;
                }
                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                string filePath = Path.Combine(pluginFolderPath, gameId);
                string luaFilePath = Path.Combine(pluginFolderPath, $"{gameId}.lua");
                string luadFilePath = Path.Combine(pluginFolderPath, $"{gameId}.luad");

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
                else if (File.Exists(luadFilePath))
                {
                    File.Delete(luadFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {luadFilePath}{Environment.NewLine}");
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

                string steamPath = GetSteamPathFromRegistry();
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
                string luadFilePath = Path.Combine(pluginFolderPath, $"{gameId}.luad");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Looking for files at: {luaFilePath} and {luadFilePath}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Files exist: lua={File.Exists(luaFilePath)}, luad={File.Exists(luadFilePath)}{Environment.NewLine}");

                bool fileDeleted = false;

                try
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
                        fileDeleted = true;
                    }

                    if (File.Exists(luadFilePath))
                    {

                        FileInfo fileInfo = new FileInfo(luadFilePath);
                        if ((fileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        File.Delete(luadFilePath);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted {luadFilePath}{Environment.NewLine}");
                        fileDeleted = true;
                    }
                }
                catch (UnauthorizedAccessException)
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Access denied when trying to delete game file. Need admin privileges.{Environment.NewLine}");

                    try
                    {

                        string batchFilePath = Path.Combine(Path.GetTempPath(), "DeleteGameFile.bat");
                        string batchContent = $@"@echo off
echo Deleting game files for {gameId}...
if exist ""{luaFilePath}"" del /f ""{luaFilePath}""
if exist ""{luadFilePath}"" del /f ""{luadFilePath}""
echo Files deleted, press any key to continue...
pause > nul";

                        File.WriteAllText(batchFilePath, batchContent);

                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = batchFilePath,
                            Verb = "runas",
                            UseShellExecute = true
                        };

                        Process.Start(psi);

                        await UpdateLogMessage($"Please confirm the administrator permission request to delete game {gameId} files", "warning");
                        return;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Failed to restart with admin privileges: {ex.Message}{Environment.NewLine}");
                        await UpdateLogMessage($"Error: Cannot delete game file. Please run the application as administrator.", "error");
                        return;
                    }
                }

                if (fileDeleted)
                {

                    ScanPluginGames(pluginFolderPath);
                    await UpdateLogMessage($"File for game {gameId} removed successfully", "success");
                }
                else
                {
                    await UpdateLogMessage($"No files found for game {gameId}", "warning");
                }
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

                string steamPath = GetSteamPathFromRegistry();
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

                userDataCheckTimer = new System.Threading.Timer(CheckUserDataFile, null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: User data monitoring set up for {Path.Combine(userDataPath, userDataFile)}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error setting up user data monitoring: {ex.Message}{Environment.NewLine}");
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
                await RefreshUserDataFromFile();
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

        private async Task<bool> CheckForMaintenanceMode()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for maintenance mode using simple endpoint...{Environment.NewLine}");

                string maintenanceCheckUrl = $"{apiUrl}/api/v3/admin/simple-maintenance";

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);

                    var response = await client.GetAsync(maintenanceCheckUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        dynamic result = Newtonsoft.Json.JsonConvert.DeserializeObject(responseContent);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Maintenance check response: {responseContent}{Environment.NewLine}");

                        bool isMaintenanceMode = result.maintenance == true;

                        if (isMaintenanceMode)
                        {

                            restrictionReason = "Server is currently under maintenance. Please try again later.";

                            if (result.config != null && result.config.expected_duration != null)
                            {
                                string expectedDuration = result.config.expected_duration.ToString();
                                restrictionReason += $" Expected completion: {expectedDuration}";
                            }

                            string expectedTime = "Unknown";
                            if (result.config != null && result.config.expected_duration != null)
                            {
                                expectedTime = System.Net.WebUtility.UrlEncode(result.config.expected_duration.ToString());
                            }

                            string message = System.Net.WebUtility.UrlEncode(restrictionReason);
                            restrictionRedirectUrl = $"/maintenance.html?expected={expectedTime}&message={message}";

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server is in maintenance mode: {restrictionReason}{Environment.NewLine}");

                            return true;
                        }

                        return false;
                    }
                    else
                    {

                        return await CheckMaintenanceFallback();
                    }
                }
            }
            catch (Exception ex)
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception in maintenance check: {ex.Message}, trying fallback...{Environment.NewLine}");
                return await CheckMaintenanceFallback();
            }
        }

        private async Task<bool> CheckMaintenanceFallback()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using fallback maintenance check...{Environment.NewLine}");

                string hwid = string.Empty;
                string userDataPath = @"C:\GFK\user_data.json";
                if (File.Exists(userDataPath))
                {
                    try
                    {
                        string json = File.ReadAllText(userDataPath);
                        dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                        hwid = userData?.hwid ?? userData?.device_id ?? GetDeviceId();
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error reading user data for maintenance check: {ex.Message}{Environment.NewLine}");
                        hwid = GetDeviceId();
                    }
                }
                else
                {
                    hwid = GetDeviceId();
                }

                string restrictionUrl = $"{apiUrl}/api/v3/restriction";

                using (var client = new System.Net.Http.HttpClient())
                {

                    var requestBody = new Dictionary<string, string>();

                    if (!string.IsNullOrEmpty(hwid))
                    {
                        requestBody["hwid"] = hwid;
                    }

                    string jsonBody = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
                    var content = new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(restrictionUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        dynamic result = Newtonsoft.Json.JsonConvert.DeserializeObject(responseContent);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fallback maintenance check response: {responseContent}{Environment.NewLine}");

                        if (result.maintenance == true)
                        {

                            restrictionReason = result.maintenance_message ?? "Server is currently under maintenance.";
                            restrictionRedirectUrl = result.maintenance_url ?? "";

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server is in maintenance mode (fallback): {restrictionReason}{Environment.NewLine}");

                            return true;
                        }

                        return false;
                    }
                    else
                    {

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in fallback maintenance check: {response.StatusCode}{Environment.NewLine}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception in fallback maintenance check: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private void LoadMaintenancePage()
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

                    string encodedMessage = System.Net.WebUtility.UrlEncode(restrictionReason);
                    string expectedTime = System.Net.WebUtility.UrlEncode("To be determined");

                    redirectUrl = $"{apiUrl}/maintenance.html?message={encodedMessage}&expected={expectedTime}&from=dashboard";
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading maintenance page: {redirectUrl}{Environment.NewLine}");

                if (webView?.CoreWebView2 != null)
                {

                    webView.CoreWebView2.WebMessageReceived -= MaintenanceWebMessageHandler;
                    webView.CoreWebView2.WebMessageReceived += MaintenanceWebMessageHandler;
                }

                webView.CoreWebView2.Navigate(redirectUrl);

                StopRestrictionCheckTimer();

                StartMaintenanceStatusCheck();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading maintenance page: {ex.Message}{Environment.NewLine}");

                try
                {
                    webView.CoreWebView2.Navigate($"about:blank");
                    webView.CoreWebView2.NavigationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess)
                        {
                            webView.CoreWebView2.ExecuteScriptAsync($@"
                                document.body.innerHTML = '<div style=""font-family: Arial, sans-serif; padding: 20px; text-align: center;"">' +
                                '<h2 style=""color: #f39c12;"">Maintenance in Progress</h2>' +
                                '<p>{restrictionReason.Replace("'", "\\'")}</p>' +
                                '<p>Please try again later.</p>' +
                                '</div>';
                                document.body.style.backgroundColor = '#222';
                                document.body.style.color = '#fff';
                            ");
                        }
                    };
                }
                catch
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Unable to show maintenance page, closing application{Environment.NewLine}");
                    this.Close();
                }
            }
        }

        private void MaintenanceWebMessageHandler(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Received message from maintenance page: {message}{Environment.NewLine}");

                if (message == "maintenance:completed")
                {

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Maintenance completed, reloading dashboard{Environment.NewLine}");

                    StopMaintenanceStatusCheck();

                    LoadDashboardAsync().ConfigureAwait(false);
                }
                else if (message == "close")
                {
                    this.Close();
                }
                else if (message == "minimize")
                {
                    this.WindowState = FormWindowState.Minimized;
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

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Resize request from maintenance page: {width}x{height}{Environment.NewLine}");

                            this.Size = new Size(width, height);

                            this.CenterToScreen();
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing resize message from maintenance page: {ex.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error handling maintenance message: {ex.Message}{Environment.NewLine}");
            }
        }

        private System.Threading.Timer maintenanceCheckTimer;

        private void StartMaintenanceStatusCheck()
        {
            try
            {

                StopMaintenanceStatusCheck();

                maintenanceCheckTimer = new System.Threading.Timer(
                    MaintenanceStatusCheckCallback,
                    null,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30)
                );

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Started maintenance status check timer{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error starting maintenance check timer: {ex.Message}{Environment.NewLine}");
            }
        }

        private void StopMaintenanceStatusCheck()
        {
            try
            {
                if (maintenanceCheckTimer != null)
                {
                    maintenanceCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    maintenanceCheckTimer.Dispose();
                    maintenanceCheckTimer = null;

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Stopped maintenance status check timer{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error stopping maintenance check timer: {ex.Message}{Environment.NewLine}");
            }
        }

        private void MaintenanceStatusCheckCallback(object state)
        {
            try
            {

                this.BeginInvoke(new Action(() =>
                {

                    MaintenanceStatusCheckAsync();
                }));
            }
            catch (Exception ex)
            {

                if (!this.IsDisposed && !this.Disposing)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error in maintenance status timer callback: {ex.Message}{Environment.NewLine}");
                }
            }
        }

        private async void MaintenanceStatusCheckAsync()
        {
            try
            {

                bool isMaintenanceMode = await CheckForMaintenanceMode();

                if (!isMaintenanceMode)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                $"{DateTime.Now}: Maintenance mode has ended. Reloading dashboard.{Environment.NewLine}");

                    await InitializeWebViewAsync();

                    StopMaintenanceStatusCheck();
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in maintenance status check: {ex.Message}{Environment.NewLine}");
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

        private bool wasInactive = false;

        private async void Dashboard_Activated(object sender, EventArgs e)
        {
            try
            {

                if (wasInactive && webView?.CoreWebView2 != null)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Форма активирована после неактивности, выполняем мгновенное обновление WebView{Environment.NewLine}");

                    wasInactive = false;

                    webView.Focus();

                    FastRefreshWebView();
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Ошибка в обработчике активации формы: {ex.Message}{Environment.NewLine}");
            }
        }

        private void Dashboard_Deactivate(object sender, EventArgs e)
        {
            try
            {

                wasInactive = true;
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                $"{DateTime.Now}: Форма деактивирована, установлен флаг неактивности{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Ошибка в обработчике деактивации формы: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task FastRefreshWebView()
        {
            try
            {

                if (!this.InvokeRequired)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Быстрое обновление WebView2 (UI поток){Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Запрос на быстрое обновление WebView2 из фонового потока, переключаемся на UI поток{Environment.NewLine}");

                    this.Invoke(new Action(async () =>
                    {
                        await FastRefreshWebView();
                    }));
                    return;
                }

                if (webView != null && webView.CoreWebView2 != null)
                {

                    await webView.CoreWebView2.ExecuteScriptAsync(@"
                        try {

                            const overlay = document.getElementById('loading-overlay');
                            if (overlay) {
                                overlay.classList.add('hidden');
                                overlay.style.display = 'none';
                            }

                            if (document && document.body) {

                                document.body.style.visibility = 'visible';
                                document.body.style.opacity = '1';

                                const animations = document.getAnimations ? document.getAnimations() : [];
                                animations.forEach(animation => {

                                    if (animation.play && animation.currentTime !== undefined) {

                                        if (animation.currentTime < 100) {
                                            animation.currentTime = animation.effect && animation.effect.getTiming ? 
                                                animation.effect.getTiming().duration * 0.2 : 200;
                                        }
                                        animation.play();
                                    }
                                });

                                if (typeof completeLoading === 'function') {
                                    completeLoading();
                                }

                                if (typeof startDeferredAnimations === 'function') {
                                    startDeferredAnimations();
                                }

                                console.log('UI instantly refreshed with animations preserved');
                            }
                        } catch (error) {
                            console.error('Error in fast refresh:', error);
                        }
                    ");

                    webView.Focus();

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Быстрое обновление WebView2 выполнено{Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Невозможно выполнить быстрое обновление: webView или CoreWebView2 равны null{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Ошибка при быстром обновлении WebView2: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
            }
        }
    }
}