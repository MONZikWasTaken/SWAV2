using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace SWA
{
    public partial class Dashboard : Form
    {
        private WebView2 webView;
        
        // Static version that will be updated manually with each release
        private const string APP_VERSION = "Git1.3";

        // For window dragging
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        private string selectedFilePath = null;

        public Dashboard()
        {
            InitializeComponent();
            InitializeWebView();

            // Set form properties
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "SWA V2 - Dashboard";
        }

        private async void InitializeWebView()
        {
            try
            {
                // Create a fresh WebView2 control
                this.Controls.Clear();

                webView = new WebView2();
                webView.Dock = DockStyle.Fill;
                webView.AllowExternalDrop = false;

                // Ensure WebView2 gets focus
                webView.Focus();

                // Add it to the form
                this.Controls.Add(webView);

                // Initialize WebView2 environment
                var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWA_V2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Disable default context menu and dev tools
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Handle web messages from JavaScript
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Navigate to the dashboard
                LoadDashboard();

                // Add event handlers for focus and mouse events
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

        private void LoadDashboard()
        {
            try
            {
                string dashboardPath = Path.Combine(Application.StartupPath, "UI", "dashboard.html");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Loading dashboard from: {dashboardPath}{Environment.NewLine}");

                webView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
                webView.CoreWebView2.Navigate(new Uri(dashboardPath).ToString());
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading dashboard: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Log navigation completion
            File.AppendAllText(@"C:\GFK\errorlog.txt",
                $"{DateTime.Now}: Dashboard navigation completed. Success: {e.IsSuccess}, Error code: {e.WebErrorStatus}{Environment.NewLine}");

            if (e.IsSuccess)
            {
                // Wait for DOM to be fully loaded
                await System.Threading.Tasks.Task.Delay(500);

                // Set WebView as focused element
                webView.Focus();
                
                // Immediately set the current version to prevent "Loading..."
                await webView.CoreWebView2.ExecuteScriptAsync($@"
                    try {{
                        // Initialize version variables and update UI
                        window.currentVersion = '{APP_VERSION}';
                        
                        const versionBadge = document.getElementById('version-badge');
                        if (versionBadge) versionBadge.textContent = '{APP_VERSION}';
                        
                        const currentVersionEl = document.getElementById('current-version');
                        if (currentVersionEl) currentVersionEl.textContent = '{APP_VERSION}';
                        
                        window.autoUpdateEnabled = true;
                        
                        console.log('Version initialized to: {APP_VERSION}');
                    }} catch (error) {{
                        console.error('Error initializing version:', error);
                    }}
                ");

                // Run script to ensure interactivity works
                await webView.CoreWebView2.ExecuteScriptAsync(@"
                    try {
                        // Test click handler
                        document.body.click();
                        
                        // Make sure event handlers are attached
                        if (typeof initializeEventListeners === 'function') { 
                            initializeEventListeners(); 
                        }
                        
                        // Force repaint to ensure UI is responsive
                        document.body.style.opacity = '0.99';
                        setTimeout(() => {
                            document.body.style.opacity = '1';
                        }, 100);
                        
                        console.log('Dashboard initialized successfully');
                    } catch (error) {
                        console.error('Error initializing dashboard:', error);
                    }
                ");

                // Load user info
                LoadUserData();

                // Detect and send Steam path to the UI
                GetSteamInstallPath();
                
                // Check for application updates (don't show notification on startup)
                await CheckForUpdates(false);
            }
            else
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Dashboard navigation failed with error: {e.WebErrorStatus}{Environment.NewLine}");
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
                    // Parse the JSON and update UI with the new approach
                    dynamic userData = Newtonsoft.Json.JsonConvert.DeserializeObject(json);

                    // Send the complete user data to the WebView for client-side processing
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            // Use the new processServerResponse function to handle all user data
                            if (typeof processServerResponse === 'function') {{
                                processServerResponse({json});
                            }} else {{
                                // Fallback to old method if the new function isn't available
                                const username = document.querySelector('.username');
                                if (username) username.textContent = '{userData.username ?? "User"}';
                                
                                const userPlan = document.querySelector('.user-plan');
                                if (userPlan) userPlan.textContent = '{userData.status ?? "Standard"}';
                                
                                // Update the plan badge based on the user's plan
                                if (typeof updatePlanBadge === 'function') {{
                                    updatePlanBadge();
                                }}
                            }}
                            
                            console.log('User data updated successfully');
                            
                            // Complete the loading process if still showing
                            if (typeof completeLoading === 'function') {{
                                completeLoading();
                            }}
                        }} catch (error) {{
                            console.error('Error updating user data:', error);
                        }}
                    ");

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Updated dashboard with user data{Environment.NewLine}");
                }
                else
                {
                    // If no user data found, still complete the loading
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
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error loading user data: {ex.Message}{Environment.NewLine}");

                // Complete loading even on error
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

        // New method to handle processing server responses in real-time
        public async Task ProcessServerResponse(string serverResponse)
        {
            try
            {
                // Extract the actual JSON part from the log entry, if needed
                string jsonData = serverResponse;
                if (serverResponse.Contains("Server response:"))
                {
                    int jsonStart = serverResponse.IndexOf('{');
                    if (jsonStart >= 0)
                    {
                        jsonData = serverResponse.Substring(jsonStart);
                    }
                }

                // Save the response to user_data.json
                File.WriteAllText(@"C:\GFK\user_data.json", jsonData);

                // Send the data to the WebView for processing
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

                // Log the successful processing
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Processed server response and updated user data{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error processing server response: {ex.Message}{Environment.NewLine}");
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.WebMessageAsJson.Trim('"');

            // Log all incoming messages for debugging
            File.AppendAllText(@"C:\GFK\dashboard_message_log.txt", $"{DateTime.Now}: Message received: {message}{Environment.NewLine}");

            // Check if this is a server response message and process it
            if (message.Contains("Server response:"))
            {
                // Process server response (save to json and update UI)
                ProcessServerResponse(message).ConfigureAwait(false);
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
            else if (message.StartsWith("swa:"))
            {
                bool enableSwa = message.Substring("swa:".Length) == "enable";
                // Handle SWA enable/disable
                SetSwaStatus(enableSwa);
            }
            else if (message.StartsWith("toggleGame:"))
            {
                // Format: toggleGame:gameId:isEnabled
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
                // Open file browser dialog
                BrowseForFile();
            }
            else if (message.StartsWith("copyFile:"))
            {
                // Format: copyFile:destinationPath
                string destinationPath = message.Substring("copyFile:".Length);
                CopySelectedFileTo(destinationPath);
            }
            else if (message == "logout")
            {
                // Handle logout - close dashboard and return to login form
                this.DialogResult = DialogResult.OK;
                this.Close();

                // Show login form again
                LoginForm loginForm = new LoginForm();
                loginForm.Show();
            }
            else if (message == "getSteamPath")
            {
                // Respond to request for Steam path
                GetSteamInstallPath();
            }
            else if (message == "checkPlugin")
            {
                // Get latest Steam path and check for plugin
                GetSteamInstallPath();
            }
            else if (message == "scanGames")
            {
                // Get Steam path and scan for games
                GetSteamInstallPath();
            }
            else if (message == "getErrorLog")
            {
                // Read the error log file and send it to UI
                SendErrorLogToUI();
            }
            else if (message == "clearErrorLog")
            {
                // Clear the error log file
                ClearErrorLog();
            }
            // Handle version check requests
            else if (message == "checkForUpdates" || message.StartsWith("checkForUpdates:"))
            {
                bool showNotification = message.Contains(":") && message.Split(':')[1] == "true";
                // Check for updates with the requested notification preference
                CheckForUpdates(showNotification).ConfigureAwait(false);
            }
            else if (message == "getCurrentVersion")
            {
                // Send the app version to the UI
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
                // Format: autoUpdate:true/false
                bool enableAutoUpdate = message.Substring("autoUpdate:".Length) == "true";
                File.AppendAllText(@"C:\GFK\errorlog.txt", 
                    $"{DateTime.Now}: Auto-update preference set to: {(enableAutoUpdate ? "enabled" : "disabled")}{Environment.NewLine}");
            }
            else if (message.StartsWith("test:"))
            {
                // Handle test messages
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Test message received: {message}{Environment.NewLine}");
            }
        }

        private void SetSwaStatus(bool enabled)
        {
            // Implement SWA status change logic
            Console.WriteLine($"SWA has been {(enabled ? "enabled" : "disabled")}");
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: SWA has been {(enabled ? "enabled" : "disabled")}{Environment.NewLine}");

            // Additional logic to actually enable/disable SWA functionality
        }

        private async void GetSteamInstallPath()
        {
            try
            {
                string steamPath = "Not found";

                // Try to read Steam installation path from registry
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

                // Log the found path
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Steam path detected: {steamPath}{Environment.NewLine}");

                // Also check for the plugin
                CheckSteamPlugin(steamPath);

                // Send the path to the WebView
                if (webView != null && webView.CoreWebView2 != null)
                {
                    // Escape any backslashes for JavaScript
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

                // Check for hid.dll in the Steam folder
                string hidDllPath = Path.Combine(steamPath, "hid.dll");
                bool hidDllExists = File.Exists(hidDllPath);

                // Check for plugin path
                string pluginFolderPath = Path.Combine(steamPath, "config", "stplug-in");
                bool pluginFolderExists = Directory.Exists(pluginFolderPath);

                // Log findings
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Plugin detection - hid.dll: {hidDllExists}, plugin folder: {pluginFolderExists}{Environment.NewLine}");

                // Prepare status message
                string statusMessage;
                if (hidDllExists && pluginFolderExists)
                {
                    statusMessage = "Detected (hid.dll and plugin folder present)";
                    // Scan for games if the plugin folder exists
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
                    // Scan for games if the plugin folder exists
                    ScanPluginGames(pluginFolderPath);
                }
                else
                {
                    statusMessage = "Not detected";
                }

                // Update UI
                await UpdatePluginStatus(statusMessage);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error checking plugin: {ex.Message}{Environment.NewLine}");
                await UpdatePluginStatus("Error checking plugin");
            }
        }

        // IMPORTANT: These methods must REPLACE the existing ScanPluginGames and ToggleGame methods in the 
        // Dashboard.cs file of the main application. They won't work if they're in a separate file.
        // Modified methods for SWA Dashboard.cs to use .lua/.luad files instead of .st/.std

        private async void ScanPluginGames(string pluginFolderPath)
        {
            try
            {
                // Get all .lua and .luad files in the plugin directory
                string[] luaFiles = Directory.GetFiles(pluginFolderPath, "*.lua");
                string[] luadFiles = Directory.GetFiles(pluginFolderPath, "*.luad");

                // Filter to only numeric-named files
                var gameFiles = new List<string>();
                var debugList = new List<string>();

                foreach (string file in luaFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    debugList.Add(fileName); // Add to debug list for complete reporting

                    // Check if the filename contains only numbers
                    if (fileName.All(char.IsDigit))
                    {
                        gameFiles.Add(fileName);
                    }
                }

                // Also include disabled games (.luad files)
                foreach (string file in luadFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    debugList.Add(fileName); // Add to debug list for complete reporting

                    // Check if the filename contains only numbers
                    if (fileName.All(char.IsDigit))
                    {
                        gameFiles.Add(fileName + ".luad"); // Add with .luad extension to mark as disabled
                    }
                }

                // Sort game files numerically (ignoring the .luad extension)
                gameFiles.Sort((a, b) =>
                {
                    string aClean = a.EndsWith(".luad") ? a.Substring(0, a.Length - 5) : a;
                    string bClean = b.EndsWith(".luad") ? b.Substring(0, b.Length - 5) : b;
                    return int.Parse(aClean).CompareTo(int.Parse(bClean));
                });

                // Log findings
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Found {gameFiles.Count} game files: {string.Join(", ", gameFiles)}{Environment.NewLine}");
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All .lua/.luad files: {string.Join(", ", debugList)}{Environment.NewLine}");

                // Update UI with found games
                if (webView != null && webView.CoreWebView2 != null)
                {
                    // Create JSON array of game IDs
                    string gameIdsJson = "[" + string.Join(",", gameFiles.Select(g => $"\"{g}\"")) + "]";
                    string debugListJson = "[" + string.Join(",", debugList.Select(g => $"\"{g}\"")) + "]";

                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                try {{
                    if (typeof updateGamesList === 'function') {{
                        updateGamesList({gameIdsJson});
                    }}
                    if (typeof updateDebugGamesList === 'function') {{
                        updateDebugGamesList({debugListJson});
                    }}
                    console.log('Game list sent to UI');
                }} catch (error) {{
                    console.error('Error updating games list:', error);
                }}
            ");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error scanning plugin games: {ex.Message}{Environment.NewLine}");
            }
        }

        private void ToggleGame(string gameId, bool enable)
        {
            try
            {
                // Get steam path to find plugin folder
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

                // Find the game file with either .lua or .luad extension
                string luaFileName = $"{gameId}.lua";
                string luadFileName = $"{gameId}.luad";
                string luaFilePath = Path.Combine(pluginFolderPath, luaFileName);
                string luadFilePath = Path.Combine(pluginFolderPath, luadFileName);

                if (enable && File.Exists(luadFilePath))
                {
                    // Rename from .luad to .lua to enable
                    File.Move(luadFilePath, luaFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} enabled (renamed from .luad to .lua){Environment.NewLine}");
                }
                else if (!enable && File.Exists(luaFilePath))
                {
                    // Rename from .lua to .luad to disable
                    File.Move(luaFilePath, luadFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} disabled (renamed from .lua to .luad){Environment.NewLine}");
                }
                else
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Could not toggle game {gameId}. Files not found: lua={File.Exists(luaFilePath)}, luad={File.Exists(luadFilePath)}{Environment.NewLine}");
                }

                // Rescan games to update the UI
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
                // Escape any special characters for JavaScript
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

        private void ToggleGame(string gameId, bool enable)
        {
            try
            {
                // Get steam path to find plugin folder
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

                // Find the game file with either .st or .std extension
                string stFileName = $"{gameId}.st";
                string stdFileName = $"{gameId}.std";
                string stFilePath = Path.Combine(pluginFolderPath, stFileName);
                string stdFilePath = Path.Combine(pluginFolderPath, stdFileName);

                if (enable && File.Exists(stdFilePath))
                {
                    // Rename from .std to .st to enable
                    File.Move(stdFilePath, stFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} enabled{Environment.NewLine}");
                }
                else if (!enable && File.Exists(stFilePath))
                {
                    // Rename from .st to .std to disable
                    File.Move(stFilePath, stdFilePath);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Game {gameId} disabled{Environment.NewLine}");
                }

                // Rescan games to update the UI
                ScanPluginGames(pluginFolderPath);
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error toggling game {gameId}: {ex.Message}{Environment.NewLine}");
            }
        }

        private async void BrowseForFile()
        {
            try
            {
                // Create OpenFileDialog
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "Select file to copy";
                    openFileDialog.Filter = "All files (*.*)|*.*";
                    openFileDialog.CheckFileExists = true;
                    openFileDialog.Multiselect = false;

                    // Show dialog and get result
                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        selectedFilePath = openFileDialog.FileName;
                        string fileName = Path.GetFileName(selectedFilePath);

                        // Send selected file information to UI
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
                // Validate we have a file selected
                if (string.IsNullOrEmpty(selectedFilePath))
                {
                    await SendErrorMessageToUI("No file selected. Please browse and select a file first.");
                    return;
                }

                // Validate source file exists
                if (!File.Exists(selectedFilePath))
                {
                    await SendErrorMessageToUI("Selected file no longer exists.");
                    return;
                }

                // Validate destination
                string steamPath = GetSteamPathFromRegistry();
                if (steamPath == "Not found" || !Directory.Exists(steamPath))
                {
                    await SendErrorMessageToUI("Steam folder not found. Please ensure Steam is installed.");
                    return;
                }

                // Determine full destination path based on provided subfolder
                string destinationDir = Path.Combine(steamPath, destinationFolder.TrimStart('\\'));

                // Create directory if it doesn't exist
                if (!Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Created directory: {destinationDir}{Environment.NewLine}");
                }

                // Get destination file path
                string fileName = Path.GetFileName(selectedFilePath);
                string destinationPath = Path.Combine(destinationDir, fileName);

                // Copy file (overwrite if exists)
                File.Copy(selectedFilePath, destinationPath, true);

                // Log success
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Copied file: {selectedFilePath} to {destinationPath}{Environment.NewLine}");

                // Send success message to UI
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

            // Try to read Steam installation path from registry
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
                    // Read the last 300 lines of the log (to prevent overwhelming the UI)
                    var lines = new List<string>();
                    using (var fs = new FileStream(errorLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lines.Add(line);
                            // Keep only the last 300 lines
                            if (lines.Count > 300)
                                lines.RemoveAt(0);
                        }
                    }

                    // Format log content with HTML styling
                    var formattedLines = new List<string>();
                    foreach (var line in lines)
                    {
                        string formattedLine = line;

                        // Format timestamp in the line if it exists
                        if (line.Contains(DateTime.Now.Year.ToString()) && line.Contains(':'))
                        {
                            int colonIndex = line.IndexOf(':');
                            if (colonIndex > 0 && colonIndex < 30) // Reasonable timestamp length
                            {
                                // Extract and wrap the timestamp
                                string timestamp = line.Substring(0, colonIndex + 3); // Include the colon and following space
                                string restOfLine = line.Substring(colonIndex + 3);

                                formattedLine = $"<span class='timestamp'>{timestamp}</span>{restOfLine}";
                            }
                        }

                        // Format error and warning messages
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

                    // Join with line breaks
                    errorLogContent = string.Join("\\n", formattedLines);
                }

                // Escape content for JavaScript (only backslashes and quotes, preserve HTML)
                errorLogContent = errorLogContent.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "");

                // Send log to UI, allow HTML formatting
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
                    // Create file with initial cleared message instead of deleting completely
                    File.WriteAllText(errorLogPath, $"{DateTime.Now}: Error log cleared via UI{Environment.NewLine}");
                }

                // No need to update UI as the next auto-refresh will show the cleared content
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error clearing error log: {ex.Message}{Environment.NewLine}");
            }
        }

        // Method to check for application updates
        private async Task CheckForUpdates(bool showNotification = false)
        {
            try
            {
                // Log the current version
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Checking for updates. Current version: {APP_VERSION}{Environment.NewLine}");
                
                // First ensure current version is displayed
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync($@"
                        try {{
                            // Update version display in UI
                            if (typeof updateVersionDisplay === 'function') {{
                                updateVersionDisplay('{APP_VERSION}');
                            }}
                            console.log('Current version set to: {APP_VERSION}');
                        }} catch (error) {{
                            console.error('Error updating version display:', error);
                        }}
                    ");
                }
                
                // Fetch the latest version from the API
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetStringAsync("https://swa-recloud.fun/static/version2.json");
                    
                    // Parse the JSON response
                    dynamic versionData = Newtonsoft.Json.JsonConvert.DeserializeObject(response);
                    string latestVersion = versionData.version;
                    
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Latest version from API: {latestVersion}{Environment.NewLine}");
                    
                    // Check if versions are different
                    bool updateAvailable = !string.Equals(APP_VERSION, latestVersion);
                    
                    // Send comparison results to the UI
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync($@"
                            try {{
                                // Store latest version
                                latestVersion = '{latestVersion}';
                                currentVersion = '{APP_VERSION}';
                                
                                // Show update notification if applicable
                                const updateAvailable = {(updateAvailable ? "true" : "false")};
                                
                                if (updateAvailable) {{
                                    // Show update indicator
                                    const indicator = document.getElementById('update-indicator');
                                    if (indicator) indicator.style.display = 'inline-block';
                                    
                                    // Show notification if requested or if auto-update is enabled
                                    if ({(showNotification ? "true" : "false")} || (typeof autoUpdateEnabled !== 'undefined' && autoUpdateEnabled)) {{
                                        if (typeof showUpdateNotification === 'function') {{
                                            showUpdateNotification('{APP_VERSION}', '{latestVersion}');
                                        }}
                                    }}
                                    
                                    console.log('Update available: {APP_VERSION} → {latestVersion}');
                                }} else {{
                                    // Hide update indicator
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
                
                // Ensure the version is displayed even on error
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
    }
}