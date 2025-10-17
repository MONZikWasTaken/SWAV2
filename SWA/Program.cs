using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.IO.Pipes;
using SWA.UI.Forms;
using SWA.Infrastructure.Protocol;

namespace SWA
{
    internal static class Program
    {
        private static Mutex appMutex;
        private const string MUTEX_NAME = "SWA_V2_SingleInstance_Mutex";
        private const string PIPE_NAME = "SWA_V2_IPC_Pipe";

        public static string ProtocolActivationCode { get; set; }
        public static string ProtocolUsername { get; set; }
        public static LoginForm MainForm { get; set; }
        public static bool PendingPluginInstall { get; set; } = false;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Register the swav2:// protocol - always update to current path
            try
            {
                ProtocolRegistration.RegisterProtocol();
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Failed to register protocol: {ex.Message}{Environment.NewLine}");
            }

            // Check if the app was launched with a protocol URL
            string activationUrl = null;
            if (args.Length > 0)
            {
                activationUrl = args[0];
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: App launched with argument: {activationUrl}{Environment.NewLine}");

                // Try to parse the protocol URL
                if (ProtocolRegistration.TryParseProtocolUrl(activationUrl, out string code, out string username))
                {
                    ProtocolActivationCode = code;
                    ProtocolUsername = username;

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Protocol URL parsed - Code: {code}, Username: {username}{Environment.NewLine}");
                }
            }

            // Try to create a mutex to ensure single instance
            bool createdNew;
            appMutex = new Mutex(true, MUTEX_NAME, out createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Another instance is running. Sending activation code via IPC...{Environment.NewLine}");

                // Send the activation URL to the existing instance
                if (!string.IsNullOrEmpty(activationUrl))
                {
                    SendToExistingInstance(activationUrl);
                }

                return; // Exit this instance
            }

            // Start the named pipe server to receive messages from future instances
            StartPipeServer();

            Application.Run(new LoginForm());

            // Release mutex when app closes
            appMutex?.ReleaseMutex();
            appMutex?.Dispose();
        }

        private static void StartPipeServer()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In))
                        {
                            File.AppendAllText(@"C:\GFK\errorlog.txt",
                                $"{DateTime.Now}: Named pipe server waiting for connection...{Environment.NewLine}");

                            await server.WaitForConnectionAsync();

                            using (var reader = new StreamReader(server))
                            {
                                string message = await reader.ReadToEndAsync();

                                File.AppendAllText(@"C:\GFK\errorlog.txt",
                                    $"{DateTime.Now}: Received message from another instance: {message}{Environment.NewLine}");

                                // Parse the URL and update the main form
                                if (ProtocolRegistration.TryParseProtocolUrl(message, out string code, out string username))
                                {
                                    ProtocolActivationCode = code;
                                    ProtocolUsername = username;

                                    // Notify the main form on the UI thread
                                    if (MainForm != null && !MainForm.IsDisposed)
                                    {
                                        MainForm.BeginInvoke(new Action(() =>
                                        {
                                            MainForm.HandleProtocolActivation(code, username);
                                        }));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(@"C:\GFK\errorlog.txt",
                            $"{DateTime.Now}: Error in pipe server: {ex.Message}{Environment.NewLine}");
                        await Task.Delay(1000); // Wait before restarting
                    }
                }
            });
        }

        private static void SendToExistingInstance(string url)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out))
                {
                    client.Connect(5000); // 5 second timeout

                    using (var writer = new StreamWriter(client))
                    {
                        writer.AutoFlush = true;
                        writer.Write(url);
                    }

                    File.AppendAllText(@"C:\GFK\errorlog.txt",
                        $"{DateTime.Now}: Successfully sent activation URL to existing instance{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error sending to existing instance: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
