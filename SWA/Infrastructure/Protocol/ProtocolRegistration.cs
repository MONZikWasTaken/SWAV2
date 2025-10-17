using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;

namespace SWA.Infrastructure.Protocol
{
    public static class ProtocolRegistration
    {
        private const string PROTOCOL_NAME = "swav2";
        private const string PROTOCOL_DESCRIPTION = "SWA V2 Protocol";

        /// <summary>
        /// Registers the swav2:// protocol for the application
        /// </summary>
        public static bool RegisterProtocol()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;

                // If the path ends with .dll, replace it with .exe
                if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = exePath.Substring(0, exePath.Length - 4) + ".exe";
                }

                // Check if protocol is already registered with a different path
                string existingPath = GetRegisteredPath();
                bool isPathDifferent = !string.Equals(existingPath, exePath, StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(existingPath) && isPathDifferent)
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Protocol path changed. Old: {existingPath}, New: {exePath}{Environment.NewLine}");
                }

                // Log the registration attempt
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Registering protocol {PROTOCOL_NAME}:// for {exePath}{Environment.NewLine}");

                // Create the protocol key
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{PROTOCOL_NAME}"))
                {
                    if (key == null)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Failed to create registry key{Environment.NewLine}");
                        return false;
                    }

                    key.SetValue("", $"URL:{PROTOCOL_DESCRIPTION}");
                    key.SetValue("URL Protocol", "");

                    // Set the icon
                    using (RegistryKey iconKey = key.CreateSubKey("DefaultIcon"))
                    {
                        iconKey?.SetValue("", $"\"{exePath}\",0");
                    }

                    // Set the command
                    using (RegistryKey commandKey = key.CreateSubKey(@"shell\open\command"))
                    {
                        commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Successfully registered protocol {PROTOCOL_NAME}://{Environment.NewLine}");

                return true;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error registering protocol: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the protocol is already registered
        /// </summary>
        public static bool IsProtocolRegistered()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{PROTOCOL_NAME}"))
                {
                    return key != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the currently registered executable path for the protocol
        /// </summary>
        private static string GetRegisteredPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{PROTOCOL_NAME}\shell\open\command"))
                {
                    if (key != null)
                    {
                        string command = key.GetValue("")?.ToString();
                        if (!string.IsNullOrEmpty(command))
                        {
                            // Extract path from command like: "C:\path\to\SWA.exe" "%1"
                            int firstQuote = command.IndexOf('"');
                            int secondQuote = command.IndexOf('"', firstQuote + 1);
                            if (firstQuote >= 0 && secondQuote > firstQuote)
                            {
                                return command.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            return null;
        }

        /// <summary>
        /// Parses a swav2://auth/CODE/USERNAME URL
        /// </summary>
        public static bool TryParseProtocolUrl(string url, out string activationCode, out string username)
        {
            activationCode = null;
            username = null;

            try
            {
                if (string.IsNullOrEmpty(url) || !url.StartsWith($"{PROTOCOL_NAME}://", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Remove protocol prefix
                string path = url.Substring($"{PROTOCOL_NAME}://".Length);

                // Expected format: auth/CODE/USERNAME
                string[] parts = path.Split('/');

                if (parts.Length >= 3 && parts[0].Equals("auth", StringComparison.OrdinalIgnoreCase))
                {
                    activationCode = parts[1];
                    username = parts[2];

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Parsed protocol URL - Code: {activationCode}, Username: {username}{Environment.NewLine}");

                    return !string.IsNullOrEmpty(activationCode);
                }

                return false;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error parsing protocol URL: {ex.Message}{Environment.NewLine}");
                return false;
            }
        }
    }
}
