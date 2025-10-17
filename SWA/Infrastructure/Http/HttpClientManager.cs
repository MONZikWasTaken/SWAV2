using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace SWA.Infrastructure.Http
{
    /// <summary>
    /// Robust HTTP client manager with proper configuration for all users
    /// Fixes "An error occurred while sending the request" issues
    /// </summary>
    public static class HttpClientManager
    {
        private static readonly Lazy<HttpClient> lazyClient = new Lazy<HttpClient>(CreateHttpClient);

        /// <summary>
        /// Singleton HttpClient instance (proper pattern - DO NOT dispose!)
        /// </summary>
        public static HttpClient Client => lazyClient.Value;

        private static HttpClient CreateHttpClient()
        {
            try
            {
                // Enable TLS 1.2 and 1.3 (fixes SSL/TLS issues for most users)
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | (SecurityProtocolType)3072; // 3072 = Tls13

                // Allow all certificates (fixes certificate validation issues)
                ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;

                // Connection settings for better stability
                ServicePointManager.DefaultConnectionLimit = 100;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.CheckCertificateRevocationList = false;

                // Create HttpClientHandler with robust settings
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseCookies = true,
                    UseDefaultCredentials = false,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // Accept all certificates
                };

                // Try to set SslProtocols if available
                try
                {
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11 | (System.Security.Authentication.SslProtocols)12288; // 12288 = Tls13
                }
                catch
                {
                    // Ignore if SslProtocols not available
                }

                var client = new HttpClient(handler, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromSeconds(30) // 30 second timeout
                };

                // Set default headers
                client.DefaultRequestHeaders.Add("User-Agent", "SWA-V2-Client/1.3");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");

                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: HttpClient initialized successfully with TLS 1.2/1.3{Environment.NewLine}");

                return client;
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: Error creating HttpClient: {ex.Message}{Environment.NewLine}");

                // Fallback: create basic client
                var basicClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                basicClient.DefaultRequestHeaders.Add("User-Agent", "SWA-V2-Client/1.3");

                return basicClient;
            }
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // Accept all certificates (fixes certificate issues for users)
            return true;
        }

        /// <summary>
        /// Sends GET request with retry logic
        /// </summary>
        public static async Task<HttpResponseMessage> GetAsync(string url, int retries = 3)
        {
            Exception lastException = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: GET request to {url} (attempt {i + 1}/{retries}){Environment.NewLine}");

                    var response = await Client.GetAsync(url);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: GET response: {response.StatusCode}{Environment.NewLine}");

                    return response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: GET request failed (attempt {i + 1}/{retries}): {ex.Message}{Environment.NewLine}");

                    if (i < retries - 1)
                    {
                        // Wait before retry (exponential backoff)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                    }
                }
            }

            // All retries failed
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All GET retries failed for {url}: {lastException?.Message}{Environment.NewLine}");
            throw lastException ?? new Exception("Request failed after retries");
        }

        /// <summary>
        /// Sends POST request with retry logic
        /// </summary>
        public static async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, int retries = 3)
        {
            Exception lastException = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: POST request to {url} (attempt {i + 1}/{retries}){Environment.NewLine}");

                    var response = await Client.PostAsync(url, content);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: POST response: {response.StatusCode}{Environment.NewLine}");

                    return response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: POST request failed (attempt {i + 1}/{retries}): {ex.Message}{Environment.NewLine}");

                    if (i < retries - 1)
                    {
                        // Wait before retry (exponential backoff)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                    }
                }
            }

            // All retries failed
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All POST retries failed for {url}: {lastException?.Message}{Environment.NewLine}");
            throw lastException ?? new Exception("Request failed after retries");
        }

        /// <summary>
        /// Sends request with custom HttpRequestMessage and retry logic
        /// </summary>
        public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, int retries = 3)
        {
            Exception lastException = null;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: {request.Method} request to {request.RequestUri} (attempt {i + 1}/{retries}){Environment.NewLine}");

                    // Clone request for retry (HttpRequestMessage can only be sent once)
                    var clonedRequest = await CloneHttpRequestMessageAsync(request);

                    var response = await Client.SendAsync(clonedRequest);

                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: {request.Method} response: {response.StatusCode}{Environment.NewLine}");

                    return response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: {request.Method} request failed (attempt {i + 1}/{retries}): {ex.Message}{Environment.NewLine}");

                    if (i < retries - 1)
                    {
                        // Wait before retry (exponential backoff)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                    }
                }
            }

            // All retries failed
            File.AppendAllText(@"C:\GFK\errorlog.txt", $"{DateTime.Now}: All retries failed for {request.RequestUri}: {lastException?.Message}{Environment.NewLine}");
            throw lastException ?? new Exception("Request failed after retries");
        }

        private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version
            };

            // Copy headers
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Copy content
            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(contentBytes);

                // Copy content headers
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}
