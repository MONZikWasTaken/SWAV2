using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;
using Newtonsoft.Json;
using System.IO;
using System.Security.Cryptography;
using System.Net;
using System.Runtime.InteropServices;

namespace SWA
{
    public partial class LoginForm : Form
    {
        private WebView2 webView;
        private static readonly HttpClient client = new HttpClient();
        private string apiUrl;
        private string loginApiUrl;
        private string deviceId;
        private const string connectionCodeFile = @"C:\GFK\connection.dat";
        private readonly byte[] encryptionKey = Encoding.UTF8.GetBytes("SWA-V2-Encryption-Key-12345");

        private bool isUserRestricted = false;
        private string restrictionReason = string.Empty;
        private string restrictionRedirectUrl = string.Empty;

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

        public LoginForm()
        {
            InitializeComponent();
            SetupForm();
            GenerateDeviceId();
            InitializeWebViewAsync().ConfigureAwait(false);

            this.Load += (s, e) => ApplyRoundedCorners();
            this.SizeChanged += (s, e) => ApplyRoundedCorners();
        }

        private void SetupForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(400, 520);
            this.Text = "SWA V2 - Login";

            string logDirectory = @"C:\GFK";
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {

                await ApiConfigManager.LoadConfigurationAsync();
                apiUrl = ApiConfigManager.Config.Api;
                loginApiUrl = ApiConfigManager.Config.LoginApi;

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Initializing WebView with API URL: {apiUrl}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using Login API URL: {loginApiUrl}{Environment.NewLine}");

                webView = new WebView2();
                webView.Dock = DockStyle.Fill;
                this.Controls.Add(webView);

                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                webView.ZoomFactor = 1.0;

                webView.ZoomFactorChanged += async (s, e) => {
                    if (webView.ZoomFactor != 1.0)
                    {
                        webView.ZoomFactor = 1.0;
                    }
                };

                webView.NavigationCompleted += async (s, e) => {
                    await webView.CoreWebView2.ExecuteScriptAsync(@"
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

                    await LoadLoginPageAsync();

                    try
                    {
                        string savedCode = LoadConnectionCode();
                        if (!string.IsNullOrEmpty(savedCode))
                        {

                            await webView.CoreWebView2.ExecuteScriptAsync($"document.getElementById('activation-code').value = '{savedCode}'; formatActivationCode();");
                            await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('remember-device').checked = true;");
                            await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Saved activation code loaded', 'success');");

                            ProcessActivation(savedCode, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading saved code: {ex.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error initializing WebView: {ex.Message}{Environment.NewLine}");
                MessageBox.Show($"Error initializing WebView: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<bool> CheckForUserRestrictions()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for user restrictions...{Environment.NewLine}");

                string restrictionUrl = $"{apiUrl}/api/v3/restriction";

                using (var request = new HttpRequestMessage(HttpMethod.Get, restrictionUrl))
                {

                    request.Headers.Add("X-Hardware-ID", deviceId);

                    var response = await client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        dynamic result = JsonConvert.DeserializeObject(responseContent);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restriction check response: {responseContent}{Environment.NewLine}");

                        if (result.is_restricted == true)
                        {
                            isUserRestricted = true;

                            if (result.ip_banned == true)
                            {
                                restrictionReason = result.ip_reason ?? "Your IP address has been blocked.";
                                restrictionRedirectUrl = result.ip_block_url ?? "";
                            }
                            else if (result.hwid_banned == true)
                            {
                                restrictionReason = result.hwid_reason ?? "Your device has been blocked.";
                                restrictionRedirectUrl = result.hwid_block_url ?? "";
                            }
                            else if (result.maintenance == true)
                            {
                                restrictionReason = result.maintenance_message ?? "Server is currently under maintenance.";
                                restrictionRedirectUrl = result.maintenance_url ?? "";
                            }
                            else
                            {
                                restrictionReason = "Access denied. Please contact support.";
                            }

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User is restricted: {restrictionReason}{Environment.NewLine}");

                            return true;
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

        private async Task<bool> CheckForMaintenanceMode()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for maintenance mode...{Environment.NewLine}");

                string restrictionUrl = $"{apiUrl}/api/v3/restriction";

                using (var request = new HttpRequestMessage(HttpMethod.Get, restrictionUrl))
                {

                    request.Headers.Add("X-Hardware-ID", deviceId);

                    var response = await client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        dynamic result = JsonConvert.DeserializeObject(responseContent);

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Maintenance check response: {responseContent}{Environment.NewLine}");

                        if (result.maintenance == true)
                        {

                            restrictionReason = result.maintenance_message ?? "Server is currently under maintenance.";
                            restrictionRedirectUrl = result.maintenance_url ?? "";

                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server is in maintenance mode: {restrictionReason}{Environment.NewLine}");

                            return true;
                        }

                        return false;
                    }
                    else
                    {

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking maintenance mode: {response.StatusCode}{Environment.NewLine}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception in maintenance check: {ex.Message}{Environment.NewLine}");
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

                    redirectUrl = $"{apiUrl}{restrictionRedirectUrl}";
                }
                else
                {

                    string encodedMessage = WebUtility.UrlEncode(restrictionReason);
                    string expectedTime = WebUtility.UrlEncode("To be determined");
                    redirectUrl = $"{apiUrl}/maintenance.html?message={encodedMessage}&expected={expectedTime}";
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading maintenance page: {redirectUrl}{Environment.NewLine}");

                webView.CoreWebView2.Navigate(redirectUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading maintenance page: {ex.Message}{Environment.NewLine}");

                try
                {
                    webView.CoreWebView2.Navigate($"about:blank");
                    webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                    {
                        if (e.IsSuccess)
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync($@"
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

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Unable to show maintenance page{Environment.NewLine}");
                }
            }
        }

        private void LoadRestrictionPage()
        {
            try
            {
                string redirectUrl;

                if (!string.IsNullOrEmpty(restrictionRedirectUrl))
                {

                    redirectUrl = $"{apiUrl}{restrictionRedirectUrl}";
                }
                else
                {

                    redirectUrl = $"{apiUrl}/access_denied.html?reason={WebUtility.UrlEncode(restrictionReason)}";
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading restriction page: {redirectUrl}{Environment.NewLine}");

                webView.CoreWebView2.Navigate(redirectUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading restriction page: {ex.Message}{Environment.NewLine}");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Unable to show restriction page, but user is still restricted{Environment.NewLine}");
            }
        }

        private async Task LoadLoginPageAsync()
        {
            try
            {
                string loginPageUrl;

                if (ApiConfigManager.Config.Ui.Local == 1)
                {

                    loginPageUrl = Path.Combine(Application.StartupPath, "UI", "login.html");
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using local login page: {loginPageUrl}{Environment.NewLine}");
                }
                else
                {

                    loginPageUrl = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.Ui.Paths.Login}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using remote login page: {loginPageUrl}{Environment.NewLine}");
                }

                webView.CoreWebView2.Navigate(new Uri(loginPageUrl).ToString());
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading login page: {ex.Message}{Environment.NewLine}");
            }
        }

        private void GenerateDeviceId()
        {
            string deviceIdPath = @"C:\GFK\device_id.txt";

            if (File.Exists(deviceIdPath))
            {
                deviceId = File.ReadAllText(deviceIdPath).Trim();
            }
            else
            {
                deviceId = $"DEV-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                Directory.CreateDirectory(@"C:\GFK");
                File.WriteAllText(deviceIdPath, deviceId);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson.Trim('"');

            File.AppendAllText(@"C:\GFK\message_log.txt", $"{DateTime.Now}: Message received: {message}{Environment.NewLine}");
            if (message.StartsWith("activate:"))
            {

                string activationData = message.Substring("activate:".Length);
                string activationCode;
                bool rememberDevice = false;

                if (activationData.Contains("|"))
                {
                    string[] parts = activationData.Split('|');
                    activationCode = parts[0];
                    bool.TryParse(parts[1], out rememberDevice);
                }
                else
                {
                    activationCode = activationData;
                }

                ProcessActivation(activationCode, rememberDevice);
            }
            else if (message == "guest-login")
            {

                ProcessGuestLogin();
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
        }

        private async void ProcessActivation(string activationCode, bool rememberDevice)
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processing activation for code: {activationCode}, Remember: {rememberDevice}{Environment.NewLine}");

                await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Connecting...', 'info');");
                await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('activate-btn').disabled = true;");

                if (await TryConnectToServers(activationCode, rememberDevice))
                {

                    CheckDeviceStatus();

                    this.Invoke((MethodInvoker)(() =>
                    {
                        Dashboard dashboardForm = new Dashboard();
                        dashboardForm.Show();
                        this.Hide();
                    }));
                }
                else
                {

                    await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('activate-btn').disabled = false;");
                }
            }
            catch (Exception ex)
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Error: {ex.Message.Replace("'", "\\'")}', 'error');");
                await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('activate-btn').disabled = false;");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection error: {ex.Message}{Environment.NewLine}");
            }
        }

        private async Task<bool> TryConnectToServers(string activationCode, bool rememberDevice)
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Trying to connect to login server: {loginApiUrl}{Environment.NewLine}");
                return await AttemptConnection(loginApiUrl, activationCode, rememberDevice);
            }
            catch (Exception ex)
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Connection error. Please try again.', 'error');");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection error in login: {ex.Message}{Environment.NewLine}");

                return false;
            }
        }

        private async Task<bool> AttemptConnection(string serverUrl, string activationCode, bool rememberDevice)
        {
            try
            {

                var data = new
                {
                    code = activationCode,
                    device_id = deviceId,
                    device_name = Environment.MachineName,
                    device_os = Environment.OSVersion.ToString()
                };

                var json = JsonConvert.SerializeObject(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending request to {serverUrl}/api/launcher/connect: {json}{Environment.NewLine}");

                var response = await client.PostAsync($"{serverUrl}/api/launcher/connect", content);

                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server error: Method Not Allowed. Trying alternative endpoint...{Environment.NewLine}");

                    try
                    {
                        var alternativeResponse = await client.GetAsync($"{serverUrl}/api/launcher/status?code={activationCode}&device_id={deviceId}");
                        if (alternativeResponse.IsSuccessStatusCode)
                        {
                            var alternativeContent = await alternativeResponse.Content.ReadAsStringAsync();
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Alternative endpoint response: {alternativeContent}{Environment.NewLine}");
                        }
                    }
                    catch (Exception altEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error using alternative endpoint: {altEx.Message}{Environment.NewLine}");
                    }

                    return false;
                }

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseContent);

                    string requestData = $"{DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")}: Sending request to {serverUrl}/api/launcher/connect: {json}";
                    string responseData = $"{DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")}: Server response: {responseContent}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", requestData + Environment.NewLine);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", responseData + Environment.NewLine);

                    bool isSuccess = false;

                    try
                    {
                        if (result != null && result.success != null)
                        {
                            isSuccess = (bool)result.success;
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking success field: {ex.Message}{Environment.NewLine}");
                    }

                    if (isSuccess)
                    {

                        string userId = null;
                        try
                        {
                            userId = result.user_id;
                            if (string.IsNullOrEmpty(userId))
                            {
                                throw new Exception("user_id is empty");
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting user_id: {ex.Message}{Environment.NewLine}");
                            await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Invalid response: Missing user information', 'error');");
                            return false;
                        }

                        if (rememberDevice)
                        {
                            SaveConnectionCode(activationCode);
                        }

                        using (var sw = new StreamWriter(@"C:\GFK\user_data.json", false))
                        {
                            await sw.WriteAsync(responseContent);
                            await sw.FlushAsync();
                        }

                        var userData = new
                        {
                            user_id = userId,
                            device_id = deviceId,
                            unique_id = result.unique_id ?? Guid.NewGuid().ToString(),
                            username = result.username ?? "User",
                            status = result.status ?? "Active"
                        };

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully authenticated user {userId}{Environment.NewLine}");

                        return true;
                    }
                    else
                    {

                        string errorMessage = "Authentication failed";

                        try
                        {
                            if (result != null && result.error != null)
                            {
                                errorMessage = result.error.ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error extracting error message: {ex.Message}{Environment.NewLine}");
                        }

                        await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('{errorMessage.Replace("'", "\\'")}', 'error');");

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Authentication failed: {errorMessage}{Environment.NewLine}");

                        if (File.Exists(connectionCodeFile))
                        {
                            File.Delete(connectionCodeFile);
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Deleted connection.dat due to authentication failure{Environment.NewLine}");
                        }

                        return false;
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server error: {response.StatusCode}, Content: {errorContent}{Environment.NewLine}");

                    await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Server error: {response.StatusCode}', 'error');");

                    return false;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection error: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");

                await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Connection error: {ex.Message.Replace("'", "\\'")}', 'error');");

                return false;
            }
        }

        private void CheckDeviceStatus()
        {
            Task.Run(async () =>
            {
                try
                {

                    while (true)
                    {
                        bool isDisconnected = await IsDeviceDisconnected(loginApiUrl);

                        if (isDisconnected)
                        {

                            this.Invoke((MethodInvoker)(() =>
                            {

                                try
                                {
                                    if (File.Exists(connectionCodeFile))
                                    {
                                        File.Delete(connectionCodeFile);
                                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully deleted connection.dat file{Environment.NewLine}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error deleting connection.dat: {ex.Message}{Environment.NewLine}");
                                }

                                MessageBox.Show(
                                    "This device has been disconnected from the web interface. You need to reconnect.",
                                    "Device Disconnected",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);

                                this.Show();
                                webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Device disconnected. Please reconnect.', 'error');");
                            }));

                            break;
                        }

                        await Task.Delay(10000);
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in CheckDeviceStatus: {ex.Message}{Environment.NewLine}");
                }
            });
        }

        private async Task<bool> IsDeviceDisconnected(string serverUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    return false;
                }

                var response = await client.GetAsync($"{serverUrl}/api/user/device-status?device_id={deviceId}");

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseContent);

                    if (result.success == true && result.disconnected == true)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async void ProcessGuestLogin()
        {
            try
            {

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processing guest login{Environment.NewLine}");

                await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Logging in as guest...', 'info');");

                var userData = new
                {
                    user_id = $"guest_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    device_id = deviceId,
                    unique_id = $"guest_{deviceId}",
                    username = "Guest",
                    status = "Standard",
                    is_guest = true,
                    expiration_date = "Never"
                };

                string userDataJson = JsonConvert.SerializeObject(userData);
                File.WriteAllText(@"C:\GFK\user_data.json", userDataJson);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully created guest account - No API requests will be sent{Environment.NewLine}");

                this.Invoke((MethodInvoker)(() =>
                {
                    Dashboard dashboardForm = new Dashboard();
                    dashboardForm.Show();
                    this.Hide();
                }));
            }
            catch (Exception ex)
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Error: {ex.Message.Replace("'", "\\'")}', 'error');");
                await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('activate-btn').disabled = false;");
                await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('guest-login-btn').disabled = false;");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Guest login error: {ex.Message}{Environment.NewLine}");
            }
        }

        private void SaveConnectionCode(string code)
        {
            try
            {

                string directoryPath = Path.GetDirectoryName(connectionCodeFile);
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                try
                {
                    using (Aes aes = Aes.Create())
                    {
                        byte[] key = new byte[32];
                        Array.Copy(encryptionKey, key, Math.Min(encryptionKey.Length, key.Length));
                        aes.Key = key;
                        aes.GenerateIV();
                        ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                        using (FileStream fs = new FileStream(connectionCodeFile, FileMode.Create))
                        {
                            fs.Write(aes.IV, 0, aes.IV.Length);
                            using (CryptoStream cs = new CryptoStream(fs, encryptor, CryptoStreamMode.Write))
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(code);
                            }
                        }
                    }
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection code saved (encrypted) successfully to {connectionCodeFile}{Environment.NewLine}");
                }
                catch (Exception exEnc)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error saving encrypted connection code: {exEnc.Message}{Environment.NewLine}");

                    try
                    {
                        File.WriteAllText(connectionCodeFile, code);
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Saved connection code as plain text fallback{Environment.NewLine}");
                    }
                    catch (Exception fallbackEx)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fallback save also failed: {fallbackEx.Message}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error in SaveConnectionCode: {ex.Message}{Environment.NewLine}");
            }
        }

        private string LoadConnectionCode()
        {
            if (!File.Exists(connectionCodeFile))
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection code file does not exist: {connectionCodeFile}{Environment.NewLine}");
                return string.Empty;
            }

            try
            {
                using (Aes aes = Aes.Create())
                {
                    byte[] key = new byte[32];
                    Array.Copy(encryptionKey, key, Math.Min(encryptionKey.Length, key.Length));
                    aes.Key = key;
                    using (FileStream fs = new FileStream(connectionCodeFile, FileMode.Open))
                    {
                        if (fs.Length <= aes.IV.Length)
                            throw new Exception("File is too small to contain a valid IV");
                        byte[] iv = new byte[aes.IV.Length];
                        fs.Read(iv, 0, iv.Length);
                        ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, iv);
                        using (CryptoStream cs = new CryptoStream(fs, decryptor, CryptoStreamMode.Read))
                        using (StreamReader sr = new StreamReader(cs))
                        {
                            string code = sr.ReadToEnd();
                            if (!string.IsNullOrEmpty(code))
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully loaded encrypted connection code{Environment.NewLine}");
                                return code;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading encrypted connection code: {ex.Message}{Environment.NewLine}");

                try
                {
                    string code = File.ReadAllText(connectionCodeFile);
                    if (!string.IsNullOrEmpty(code))
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loaded connection code as plain text fallback{Environment.NewLine}");
                        return code;
                    }
                }
                catch (Exception fallbackEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fallback load also failed: {fallbackEx.Message}{Environment.NewLine}");
                }
            }
            return string.Empty;
        }
    }
}