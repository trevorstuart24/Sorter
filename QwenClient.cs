// QwenClient.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sorter
{
    /// <summary>
    /// Wrapper around LM Studio's OpenAI-compatible /v1/chat/completions endpoint
    /// for Qwen (or other vision models) using image_url.
    /// 
    /// Behavior:
    /// - Always sends the JPEG as an image_url in the user message.
    /// - If RunConfig.LmSystemPrompt is non-empty, sends it as a system message.
    /// - If RunConfig.LmSystemPrompt is empty, sends NO system message at all,
    ///   so LM Studio's own configured system prompt is used.
    /// - Does NOT send any extra textual prompt from this client unless you put
    ///   something in LmSystemPrompt.
    /// </summary>
    public sealed class QwenClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public QwenClient(int timeoutMs)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs <= 0 ? 30000 : timeoutMs)
            };
        }

        /// <summary>
        /// Sends a single JPEG image to the model and returns (rawText, elapsedSeconds).
        /// Bin parsing is handled separately by CartridgeMapper.
        /// </summary>
        public async Task<(string text, double secs)> ClassifyAsync(byte[] jpegBytes, RunConfig cfg)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QwenClient));

            if (jpegBytes == null || jpegBytes.Length == 0)
                throw new ArgumentException("jpegBytes is empty.", nameof(jpegBytes));

            if (cfg == null)
                throw new ArgumentNullException(nameof(cfg));

            var sw = Stopwatch.StartNew();

            string base64 = Convert.ToBase64String(jpegBytes);
            string baseUrl = string.IsNullOrWhiteSpace(cfg.LmUrl)
                ? "http://localhost:1234"
                : cfg.LmUrl.TrimEnd('/');

            // LM Studio uses OpenAI-compatible path
            string url = baseUrl + "/v1/chat/completions";

            // ===== Build user message content with explicit 'type' fields =====
            var userParts = new List<object>
            {
                // Only the image; no text content at all unless you
                // explicitly put something into the system prompt in the UI.
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = "data:image/jpeg;base64," + base64
                    }
                }
            };

            var messages = new List<object>();

            // Optional system message: ONLY if you set LmSystemPrompt in the UI.
            // If it's empty, we send no system message, so LM Studio's own system
            // prompt configuration is used.
            if (!string.IsNullOrWhiteSpace(cfg.LmSystemPrompt))
            {
                messages.Add(new
                {
                    role = "system",
                    content = cfg.LmSystemPrompt
                });
            }

            // User message with just the image
            messages.Add(new
            {
                role = "user",
                content = userParts.ToArray()
            });

            // ===== Build JSON payload =====
            var payload = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "qwen" : cfg.Model,
                ["messages"] = messages.ToArray()
            };

            if (cfg.UseTemperature)
                payload["temperature"] = cfg.Temperature;

            if (cfg.UseMaxTokens)
                payload["max_tokens"] = cfg.MaxOutputTokens;

            string json = JsonConvert.SerializeObject(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request))
                {
                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception(
                            $"LM request failed with status {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                    }

                    // ===== Parse response (OpenAI-style) =====
                    var root = JObject.Parse(body);
                    var choices = root["choices"] as JArray;
                    if (choices == null || choices.Count == 0)
                        throw new Exception("LM response missing 'choices'.");

                    var message = choices[0]["message"];
                    if (message == null)
                        throw new Exception("LM response missing 'message'.");

                    string text;
                    var contentToken = message["content"];

                    // LM Studio usually returns a simple string, but handle arrays too.
                    if (contentToken == null)
                    {
                        text = string.Empty;
                    }
                    else if (contentToken.Type == JTokenType.String)
                    {
                        text = (string)contentToken;
                    }
                    else if (contentToken.Type == JTokenType.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in (JArray)contentToken)
                        {
                            var t = part["text"] ?? part["content"];
                            if (t != null)
                            {
                                if (sb.Length > 0) sb.AppendLine();
                                sb.Append((string)t);
                            }
                        }
                        text = sb.ToString();
                    }
                    else
                    {
                        text = contentToken.ToString();
                    }

                    sw.Stop();
                    double secs = sw.Elapsed.TotalSeconds;
                    return (text, secs);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient.Dispose();
        }
    }
}
