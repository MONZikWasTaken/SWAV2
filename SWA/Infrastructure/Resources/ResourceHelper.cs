using System;
using System.IO;
using System.Reflection;

namespace SWA.Infrastructure.Resources
{
    public static class ResourceHelper
    {
        /// <summary>
        /// Loads an embedded HTML resource from the assembly directly to string (FAST!)
        /// </summary>
        /// <param name="resourceName">Name of the resource (e.g., "login.html" or "dashboard.html")</param>
        /// <returns>The HTML content as a string</returns>
        public static string LoadEmbeddedHtmlContent(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string fullResourceName = $"SWA.uisrc.{resourceName}";

                using (Stream stream = assembly.GetManifestResourceStream(fullResourceName))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException($"Embedded resource '{fullResourceName}' not found.");
                    }

                    using (StreamReader reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error loading embedded HTML content '{resourceName}': {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        /// <summary>
        /// Loads an embedded HTML resource from the assembly (legacy method for compatibility)
        /// </summary>
        /// <param name="resourceName">Name of the resource (e.g., "login.html" or "dashboard.html")</param>
        /// <returns>The HTML content as a string</returns>
        [Obsolete("Use LoadEmbeddedHtmlContent for better performance")]
        public static string LoadEmbeddedHtml(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                // Resource names follow pattern: Namespace.Folder.FileName
                string fullResourceName = $"SWA.uisrc.{resourceName}";

                using (Stream stream = assembly.GetManifestResourceStream(fullResourceName))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException($"Embedded resource '{fullResourceName}' not found. Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
                    }

                    using (StreamReader reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error loading embedded resource '{resourceName}': {ex.Message}{Environment.NewLine}");
                throw;
            }
        }

        /// <summary>
        /// Creates a temporary HTML file from embedded resource for WebView2 to load
        /// </summary>
        /// <param name="resourceName">Name of the resource</param>
        /// <returns>Path to the temporary HTML file</returns>
        public static string CreateTempHtmlFile(string resourceName)
        {
            try
            {
                string htmlContent = LoadEmbeddedHtml(resourceName);
                string tempPath = Path.Combine(Path.GetTempPath(), "SWA_V2", resourceName);

                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath));

                // Write the HTML content to temp file
                File.WriteAllText(tempPath, htmlContent);

                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Created temp HTML file: {tempPath}{Environment.NewLine}");

                return tempPath;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt",
                    $"{DateTime.Now}: Error creating temp HTML file for '{resourceName}': {ex.Message}{Environment.NewLine}");
                throw;
            }
        }
    }
}
