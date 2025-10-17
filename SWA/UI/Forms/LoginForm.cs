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
using System.Diagnostics;
using SWA.Infrastructure.Api;
using SWA.Infrastructure.Resources;
using SWA.Infrastructure.Http;
using System.Management;

namespace SWA.UI.Forms
{
    public partial class LoginForm : Form
    {
        private WebView2 webView;
        private Label loadingLabel;
        private string apiUrl; // Will be loaded from configuration
        private string loginApiUrl; // New field for login API URL
        private string deviceId;
        private const string connectionCodeFile = @"C:\GFK\connection.dat";
        private readonly byte[] encryptionKey = Encoding.UTF8.GetBytes("SWA-V2-Encryption-Key-12345");
        private FormWindowState lastWindowState = FormWindowState.Normal;

        // Restriction tracking
        private bool isUserRestricted = false;
        private string restrictionReason = string.Empty;
        private string restrictionRedirectUrl = string.Empty;

        // For window dragging
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);


        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        public const int WM_NCCALCSIZE = 0x0083;

        // Override CreateParams to add rounded corners
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x20000; // WS_MINIMIZEBOX
                return cp;
            }
        }

        // Handle window messages for maintaining rounded corners
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // DISABLED: Rounded corners cause WebView2 to break on minimize/restore
            //if (m.Msg == WM_NCCALCSIZE && m.WParam.ToInt32() == 1)
            //{
            //    ApplyRoundedCorners();
            //}
        }


        public LoginForm()
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
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error loading icon in LoginForm: {ex.Message}{Environment.NewLine}");
            }

            SetupForm();
            GenerateDeviceId();

            // Register this form as the main form for IPC
            Program.MainForm = this;

            // Try silent login BEFORE initializing WebView for maximum speed
            TryFastLoginAsync().ConfigureAwait(false);

            // DISABLED: Rounded corners cause WebView2 issues
            //this.Load += (s, e) => ApplyRoundedCorners();
            //this.SizeChanged += (s, e) => ApplyRoundedCorners();
        }

        private void SetupForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(400, 520);
            this.Text = "SWA V2 - Login";
            this.BackColor = Color.FromArgb(18, 18, 18);

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

            // Check if log directory exists
            string logDirectory = @"C:\GFK";
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
        }

        private async Task TryFastLoginAsync()
        {
            try
            {
                // Load API configuration first (required for authentication)
                await ApiConfigManager.LoadConfigurationAsync();
                apiUrl = ApiConfigManager.Config.Api;
                loginApiUrl = ApiConfigManager.Config.LoginApi;

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast login - checking for saved credentials...{Environment.NewLine}");

                // Check if launched via protocol URL first
                if (!string.IsNullOrEmpty(Program.ProtocolActivationCode))
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Fast login - found protocol activation code{Environment.NewLine}");

                    // Try to login silently with protocol code (without WebView)
                    if (await TryConnectToServersFast(Program.ProtocolActivationCode, true))
                    {
                        // Success! Go to dashboard without ever showing login form
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Fast login - protocol authentication successful, opening dashboard{Environment.NewLine}");

                        CheckDeviceStatus();
                        this.Invoke((MethodInvoker)(() =>
                        {
                            Dashboard dashboardForm = new Dashboard();
                            dashboardForm.Show();
                            this.Hide();
                        }));
                        return;
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Fast login - protocol authentication failed, initializing WebView{Environment.NewLine}");
                    }
                }
                else
                {
                    // Check for saved connection code
                    string savedCode = LoadConnectionCode();
                    if (!string.IsNullOrEmpty(savedCode))
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Fast login - found saved code, attempting authentication{Environment.NewLine}");

                        // Try to login silently with saved code (without WebView)
                        if (await TryConnectToServersFast(savedCode, true))
                        {
                            // Success! Go to dashboard without ever showing login form
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Fast login - saved code authentication successful, opening dashboard{Environment.NewLine}");

                            CheckDeviceStatus();
                            this.Invoke((MethodInvoker)(() =>
                            {
                                Dashboard dashboardForm = new Dashboard();
                                dashboardForm.Show();
                                this.Hide();
                            }));
                            return;
                        }
                        else
                        {
                            // Failed - delete invalid code
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Fast login - saved code authentication failed, deleting connection.dat{Environment.NewLine}");

                            try
                            {
                                if (File.Exists(connectionCodeFile))
                                {
                                    File.Delete(connectionCodeFile);
                                }
                            }
                            catch (Exception delEx)
                            {
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Error deleting connection.dat: {delEx.Message}{Environment.NewLine}");
                            }
                        }
                    }
                    else
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Fast login - no saved code found{Environment.NewLine}");
                    }
                }

                // If we get here, we need to show the login form - initialize WebView
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Fast login - authentication failed or no credentials, initializing WebView{Environment.NewLine}");
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in fast login: {ex.Message}{Environment.NewLine}");
                // Fallback to normal initialization
                await InitializeWebViewAsync();
            }
        }

        private async Task<bool> TryConnectToServersFast(string activationCode, bool rememberDevice)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - trying to connect to login server: {loginApiUrl}{Environment.NewLine}");
                return await AttemptConnectionFast(loginApiUrl, activationCode, rememberDevice);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - connection error: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private async Task<bool> AttemptConnectionFast(string serverUrl, string activationCode, bool rememberDevice)
        {
            try
            {
                // Prepare the data for the server
                var data = new
                {
                    code = activationCode,
                    device_id = deviceId,
                    device_name = Environment.MachineName,
                    device_os = Environment.OSVersion.ToString()
                };

                var json = JsonConvert.SerializeObject(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - sending request to {serverUrl}/api/launcher/connect{Environment.NewLine}");

                var response = await HttpClientManager.PostAsync($"{serverUrl}/api/launcher/connect", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseContent);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - server response: {responseContent}{Environment.NewLine}");

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
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - error checking success field: {ex.Message}{Environment.NewLine}");
                    }

                    if (isSuccess)
                    {
                        // Check if the user ID is present
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
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - error extracting user_id: {ex.Message}{Environment.NewLine}");
                            return false;
                        }

                        // Save connection code if remember device is checked
                        if (rememberDevice)
                        {
                            SaveConnectionCode(activationCode);
                        }

                        // Save the complete API response to user_data.json
                        using (var sw = new StreamWriter(@"C:\GFK\user_data.json", false))
                        {
                            await sw.WriteAsync(responseContent);
                            await sw.FlushAsync();
                        }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - successfully authenticated user {userId}{Environment.NewLine}");

                        return true;
                    }
                    else
                    {
                        // Handle error from server response
                        string errorMessage = "Authentication failed";
                        try
                        {
                            if (result != null && result.error != null)
                            {
                                errorMessage = result.error.ToString();
                            }
                        }
                        catch { }

                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - authentication failed: {errorMessage}{Environment.NewLine}");

                        // Delete saved connection code if authentication fails
                        if (File.Exists(connectionCodeFile))
                        {
                            File.Delete(connectionCodeFile);
                        }

                        return false;
                    }
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - server error: {response.StatusCode}{Environment.NewLine}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Fast connect - connection error: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Initializing WebView for login UI{Environment.NewLine}");

                webView = new WebView2();
                webView.Dock = DockStyle.Fill;
                this.Controls.Add(webView);

                // Hide loading label once WebView is ready
                if (loadingLabel != null)
                {
                    loadingLabel.Visible = false;
                }

                // Initialize WebView2 environment
                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Set dark background to prevent white flash
                webView.DefaultBackgroundColor = Color.FromArgb(18, 18, 18);

                // Disable default context menu and dev tools
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Prevent zooming with Ctrl+Scroll
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // Set a fixed zoom factor (1.0 = 100%)
                webView.ZoomFactor = 1.0;

                // Subscribe to zoom factor changed event to block zoom changes
                webView.ZoomFactorChanged += async (s, e) => {
                    if (webView.ZoomFactor != 1.0)
                    {
                        webView.ZoomFactor = 1.0;
                    }
                };

                // Add JavaScript to prevent zooming
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

                    // Test critical endpoints when page loads
                    if (e.IsSuccess)
                    {
                        try
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync(@"
                                (async function() {
                                    if (typeof testCriticalEndpoints === 'function') {
                                        const endpointsReachable = await testCriticalEndpoints();
                                        if (!endpointsReachable) {
                                            console.error('Critical endpoints are not reachable');
                                        }
                                    }
                                })();
                            ");
                        }
                        catch (Exception testEx)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Error testing endpoints: {testEx.Message}{Environment.NewLine}");
                        }
                    }
                };

                // Handle web messages from JavaScript
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Check for user restrictions (IP or hardware bans)
                if (await CheckForUserRestrictions())
                {
                    // User is restricted - load the restriction page instead of login
                    LoadRestrictionPage();
                }
                else
                {
                    // Not restricted - show login form
                    // If protocol activation failed, show error in UI
                    if (!string.IsNullOrEmpty(Program.ProtocolActivationCode))
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Showing login form with protocol activation error{Environment.NewLine}");
                        await LoadLoginPageAsync();

                        try
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync("toggleManualEntry();");
                            await webView.CoreWebView2.ExecuteScriptAsync($@"
                                document.getElementById('activation-code').value = '{Program.ProtocolActivationCode}';
                                handleInputChange();
                            ");
                            await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Authentication failed. Please check your code.', 'error');");
                        }
                        catch (Exception protocolEx)
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Error showing protocol error in UI: {protocolEx.Message}{Environment.NewLine}");
                        }
                    }
                    else
                    {
                        // Normal login form
                        await LoadLoginPageAsync();
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
                // Log the restriction check attempt
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for user restrictions...{Environment.NewLine}");

                // Build the request to the restriction endpoint
                string restrictionUrl = $"{apiUrl}/api/v3/restriction";

                // Create request with hardware ID in headers
                using (var request = new HttpRequestMessage(HttpMethod.Get, restrictionUrl))
                {
                    // Add headers for identification
                    request.Headers.Add("X-Hardware-ID", deviceId);

                    // Send the request
                    var response = await HttpClientManager.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        dynamic result = JsonConvert.DeserializeObject(responseContent);

                        // Log the result for debugging
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Restriction check response: {responseContent}{Environment.NewLine}");

                        // Check if user is restricted
                        if (result.is_restricted == true)
                        {
                            isUserRestricted = true;

                            // Store restriction details for display
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
                            else
                            {
                                restrictionReason = "Access denied. Please contact support.";
                            }

                            // Log the restriction
                            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: User is restricted: {restrictionReason}{Environment.NewLine}");

                            return true;
                        }

                        // Not restricted
                        return false;
                    }
                    else
                    {
                        // Log the error but don't block access (fail open)
                        File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking restrictions: {response.StatusCode}{Environment.NewLine}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error but don't block access (fail open on errors)
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Exception in restriction check: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        private void LoadRestrictionPage()
        {
            try
            {
                string redirectUrl;

                // Check if we have a specific redirect URL
                if (!string.IsNullOrEmpty(restrictionRedirectUrl))
                {
                    // Use the server-provided restriction page
                    redirectUrl = $"{apiUrl}{restrictionRedirectUrl}";
                }
                else
                {
                    // Fallback to a generic access denied page
                    redirectUrl = $"{apiUrl}/access_denied.html?reason={WebUtility.UrlEncode(restrictionReason)}";
                }

                // Log which page we're loading
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading restriction page: {redirectUrl}{Environment.NewLine}");

                // Navigate to the restriction page
                webView.CoreWebView2.Navigate(redirectUrl);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading restriction page: {ex.Message}{Environment.NewLine}");

                // Log the error but don't show a message box
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Unable to show restriction page, but user is still restricted{Environment.NewLine}");
            }
        }

        private async Task LoadLoginPageAsync()
        {
            try
            {
                // Determine if we should use local or remote UI
                if (ApiConfigManager.Config.Ui.Local == 1)
                {
                    // Use embedded UI resources - load directly to memory (INSTANT!)
                    string htmlContent = ResourceHelper.LoadEmbeddedHtmlContent("login.html");
                    webView.CoreWebView2.NavigateToString(htmlContent);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loaded embedded login page to memory (instant){Environment.NewLine}");
                }
                else
                {
                    // Use remote UI
                    string loginPageUrl = $"{apiUrl.TrimEnd('/')}{ApiConfigManager.Config.Ui.Paths.Login}";
                    webView.CoreWebView2.Navigate(new Uri(loginPageUrl).ToString());
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Using remote login page: {loginPageUrl}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading login page: {ex.Message}{Environment.NewLine}");
            }
        }

        private void GenerateDeviceId()
        {
            try
            {
                // Generate a stable hardware-based device ID
                string hardwareId = GetHardwareId();

                // Create a deterministic device ID from hardware hash
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hardwareId));
                    // Convert first 8 bytes to hex string for a compact ID
                    string hashHex = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "");
                    deviceId = $"HW-{hashHex}";
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Generated hardware-based device ID: {deviceId}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error generating hardware ID, using fallback: {ex.Message}{Environment.NewLine}");

                // Fallback: use machine name + username hash
                string fallback = $"{Environment.MachineName}_{Environment.UserName}";
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(fallback));
                    string hashHex = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "");
                    deviceId = $"FB-{hashHex}";
                }
            }
        }

        private string GetHardwareId()
        {
            StringBuilder hardwareInfo = new StringBuilder();

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

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Hardware fingerprint collected (length: {hardwareInfo.Length}){Environment.NewLine}");

                return hardwareInfo.ToString();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error collecting hardware info: {ex.Message}{Environment.NewLine}");

                // Return a fallback identifier
                return $"{Environment.MachineName}_{Environment.UserName}_{Environment.ProcessorCount}";
            }
        }

        private string GetWmiValue(string wmiClass, string wmiProperty)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT {wmiProperty} FROM {wmiClass}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
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
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: WMI query failed for {wmiClass}.{wmiProperty}: {ex.Message}{Environment.NewLine}");
            }
            return string.Empty;
        }

        private string GetMachineGuid()
        {
            try
            {
                // Windows Machine GUID from registry - very stable identifier
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
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
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Failed to get MachineGuid: {ex.Message}{Environment.NewLine}");
            }
            return string.Empty;
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
                        var websiteRequest = new HttpRequestMessage(HttpMethod.Head, "https://swacloud.com/");
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
                        var apiRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.swa-recloud.fun/api/v3/");
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

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson.Trim('"');
            // Log all incoming messages for debugging
            File.AppendAllText(@"C:\GFK\message_log.txt", $"{DateTime.Now}: Message received: {message}{Environment.NewLine}");
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
                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Sending endpoint results to JavaScript: {results}{Environment.NewLine}");

                                string script = $"if (typeof handleEndpointTestResults === 'function') {{ handleEndpointTestResults({results}); }} else {{ console.error('handleEndpointTestResults not defined'); }}";
                                await webView.CoreWebView2.ExecuteScriptAsync(script);

                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: JavaScript executed successfully{Environment.NewLine}");
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
            else if (message.StartsWith("activate:"))
            {
                // Handle activation code - now using pipe as separator
                string activationData = message.Substring("activate:".Length);
                string activationCode;
                bool rememberDevice = false;
                // Check if message includes remember device flag with pipe separator
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
                // Start the activation process
                ProcessActivation(activationCode, rememberDevice);
            }
            else if (message == "guest-login")
            {
                // Handle guest login
                ProcessGuestLogin();
            }
            else if (message == "swacloud-login")
            {
                // Handle SWA Cloud login - open authorization URL
                try
                {
                    Process.Start("https://swacloud.com/authorize");
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Opened SWA Cloud authorization URL{Environment.NewLine}");

                    // Update status in UI - DON'T show manual entry, user will be logged in automatically
                    webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Authorization page opened in browser. Waiting for login...', 'info');");

                    // Re-enable buttons - keep SWA Cloud view visible
                    webView.CoreWebView2.ExecuteScriptAsync(@"
                        document.getElementById('swacloud-login-btn').disabled = false;
                        document.getElementById('guest-login-btn').disabled = false;
                    ");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error opening SWA Cloud URL: {ex.Message}{Environment.NewLine}");
                    webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Error opening browser: {ex.Message.Replace("'", "\\'")}', 'error');");
                    webView.CoreWebView2.ExecuteScriptAsync(@"
                        document.getElementById('swacloud-login-btn').disabled = false;
                        document.getElementById('guest-login-btn').disabled = false;
                    ");
                }
            }
            else if (message == "open-support")
            {
                // Handle open support URL
                try
                {
                    Process.Start("https://swacloud.com/support");
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Opened support URL{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error opening support URL: {ex.Message}{Environment.NewLine}");
                }
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
                // Log the activation attempt
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processing activation for code: {activationCode}, Remember: {rememberDevice}{Environment.NewLine}");

                // Update UI status
                await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Connecting...', 'info');");
                await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('activate-btn').disabled = true;");

                if (await TryConnectToServers(activationCode, rememberDevice))
                {
                    // Connection successful, start monitoring device status
                    CheckDeviceStatus();

                    // Show the dashboard
                    this.Invoke((MethodInvoker)(() =>
                    {
                        Dashboard dashboardForm = new Dashboard();
                        dashboardForm.Show();
                        this.Hide();
                    }));
                }
                else
                {
                    // Re-enable the activate button
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
                // Now using Login API URL from configuration for authentication
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
                // Prepare the data for the server
                var data = new
                {
                    code = activationCode,
                    device_id = deviceId,
                    device_name = Environment.MachineName,
                    device_os = Environment.OSVersion.ToString()
                };

                var json = JsonConvert.SerializeObject(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Log the request for debugging
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Sending request to {serverUrl}/api/launcher/connect: {json}{Environment.NewLine}");

                var response = await HttpClientManager.PostAsync($"{serverUrl}/api/launcher/connect", content);

                // Handle 405 Method Not Allowed error
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Server error: Method Not Allowed. Trying alternative endpoint...{Environment.NewLine}");

                    // Try alternative endpoint
                    try
                    {
                        var alternativeResponse = await HttpClientManager.GetAsync($"{serverUrl}/api/launcher/status?code={activationCode}&device_id={deviceId}");
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

                    // Log the request and response for debugging
                    string requestData = $"{DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")}: Sending request to {serverUrl}/api/launcher/connect: {json}";
                    string responseData = $"{DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")}: Server response: {responseContent}";
                    File.AppendAllText(@"C:\GFK\errorlog.txt", requestData + Environment.NewLine);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", responseData + Environment.NewLine);

                    bool isSuccess = false;

                    // Check if the response contains a success field
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
                        // Check if the user ID is present
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

                        // Save connection code if remember device is checked
                        if (rememberDevice)
                        {
                            SaveConnectionCode(activationCode);
                        }

                        // Save the complete API response to user_data.json (async, with flush)
                        using (var sw = new StreamWriter(@"C:\GFK\user_data.json", false))
                        {
                            await sw.WriteAsync(responseContent);
                            await sw.FlushAsync();
                        }

                        // Also save the structured user data for backward compatibility
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
                        // Handle error from server response
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

                        // Delete saved connection code if authentication fails
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
                    // Check device status every 10 seconds
                    while (true)
                    {
                        bool isDisconnected = await IsDeviceDisconnected(loginApiUrl);

                        if (isDisconnected)
                        {
                            // Device has been disconnected
                            this.Invoke((MethodInvoker)(() =>
                            {
                                // Delete connection.dat file
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

                                // Show the login form again
                                this.Show();
                                webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Device disconnected. Please reconnect.', 'error');");
                            }));

                            break;
                        }

                        await Task.Delay(10000); // Wait 10 seconds before next check
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

                var response = await HttpClientManager.GetAsync($"{serverUrl}/api/user/device-status?device_id={deviceId}");

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseContent);

                    // Device is disconnected if success=true and disconnected=true
                    if (result.success == true && result.disconnected == true)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false; // Assume device is not disconnected if there's an error
            }
        }

        private async void ProcessGuestLogin()
        {
            try
            {
                // Log the guest login attempt
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processing guest login{Environment.NewLine}");

                // Update UI status
                await webView.CoreWebView2.ExecuteScriptAsync("updateStatus('Logging in as guest...', 'info');");

                // Create guest user data - Add is_guest flag
                var userData = new
                {
                    user_id = $"guest_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                    device_id = deviceId,
                    unique_id = $"guest_{deviceId}",
                    username = "Guest",
                    status = "Standard",
                    is_guest = true, // This flag will be used to identify guest users
                    expiration_date = "Never"  // No expiration for guest
                };

                // Save user data
                string userDataJson = JsonConvert.SerializeObject(userData);
                File.WriteAllText(@"C:\GFK\user_data.json", userDataJson);

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Successfully created guest account - No API requests will be sent{Environment.NewLine}");

                // Show the dashboard
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

        // Method to save connection code to an encrypted .dat file
        private void SaveConnectionCode(string code)
        {
            try
            {
                // Ensure directory exists
                string directoryPath = Path.GetDirectoryName(connectionCodeFile);
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Try to save encrypted
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
                    // Fallback: save as plain text
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

        // Method to load connection code from an encrypted .dat file
        private string LoadConnectionCode()
        {
            if (!File.Exists(connectionCodeFile))
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Connection code file does not exist: {connectionCodeFile}{Environment.NewLine}");
                return string.Empty;
            }
            // Try to load encrypted first
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
                // Fallback: try to load as plain text
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

        /// <summary>
        /// Handles protocol activation from IPC (when app is already running)
        /// </summary>
        public async void HandleProtocolActivation(string code, string username)
        {
            try
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: HandleProtocolActivation called - Code: {code}, Username: {username}{Environment.NewLine}");

                // Bring window to front
                if (this.WindowState == FormWindowState.Minimized)
                {
                    this.WindowState = FormWindowState.Normal;
                }
                this.Activate();
                this.BringToFront();

                // Check if webView is initialized
                if (webView == null || webView.CoreWebView2 == null)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: WebView not initialized yet, waiting...{Environment.NewLine}");

                    // Store the code and username for when WebView initializes
                    Program.ProtocolActivationCode = code;
                    Program.ProtocolUsername = username;
                    return;
                }

                // Login silently in background - DON'T show activation code to user
                try
                {
                    // Just show status message - no manual entry form
                    await webView.CoreWebView2.ExecuteScriptAsync($"updateStatus('Logging in as {username}...', 'info');");

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Processing background authentication for {username}...{Environment.NewLine}");

                    // Automatically process activation in background
                    ProcessActivation(code, true);
                }
                catch (Exception jsEx)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Error showing status for protocol activation: {jsEx.Message}{Environment.NewLine}");

                    // If JavaScript fails, just process the activation
                    ProcessActivation(code, true);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error in HandleProtocolActivation: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}