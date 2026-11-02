using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpriteFusion
{
    [Serializable]
    public class DeviceStartResponse
    {
        public string deviceCode;
        public string userCode;
        public string verificationUriComplete;
        public int interval = 5;
    }

    [Serializable]
    public class SessionUser
    {
        public string email;
    }

    [Serializable]
    public class TokenResponse
    {
        public string access_token;
        public string refresh_token;
        public long expires_at;
        public string token_type;
        public SessionUser user;
    }

    [Serializable]
    public class CreditsInfo
    {
        public int reserved;
        public int remaining;
    }

    [Serializable]
    public class GeneratedAsset
    {
        public string id;
        public string type;
        public string assetUrl;
        public string prompt;
        public int width;
        public int height;
        public int frameCount;
        public float fps;
        public string createdAt;
        public string updatedAt;
        public string sourceImageUrl;
        public string spritesheetUrl;
        // Place in the batch (text-to-image shuffles all but the first); -1 when the server sends none.
        public int batchIndex = -1;
    }

    [Serializable]
    public class ApiError
    {
        public string code;
        public string message;
    }

    [Serializable]
    public class GenerationEvent
    {
        public string type;
        public string status;
        public ApiError error;
        public GeneratedAsset asset;
        public CreditsInfo credits;
    }

    [Serializable]
    public class ErrorResponse
    {
        public ApiError error;
    }

    [Serializable]
    public class DeviceErrorResponse
    {
        public string error;
    }

    [Serializable]
    public class Session
    {
        public string access_token = "";
        public string refresh_token = "";
        public long expires_at;
        public string token_type = "bearer";
        public string email = "";

        public bool IsConnected =>
            !string.IsNullOrEmpty(access_token) && !string.IsNullOrEmpty(refresh_token);

        public bool IsExpiring =>
            expires_at <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;

        static string Dir => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "UserSettings", "SpriteFusion");

        static string FilePath => Path.Combine(Dir, "session.json");

        public static Session Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonUtility.FromJson<Session>(File.ReadAllText(FilePath)) ?? new Session();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SpriteFusion: could not load session: {e.Message}");
            }

            return new Session();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
        }

        public static void Delete()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }

        public void Apply(TokenResponse data)
        {
            if (data == null) return;

            access_token = data.access_token ?? "";
            refresh_token = data.refresh_token ?? "";
            expires_at = data.expires_at;
            token_type = string.IsNullOrEmpty(data.token_type) ? "bearer" : data.token_type;
            if (data.user != null && !string.IsNullOrEmpty(data.user.email))
                email = data.user.email;
            Save();
        }
    }

    public readonly struct ApiResponse
    {
        public readonly int Status;
        public readonly string Body;
        public readonly byte[] Bytes;

        public ApiResponse(int status, string body, byte[] bytes = null)
        {
            Status = status;
            Body = body;
            Bytes = bytes;
        }

        public bool Ok => Status >= 200 && Status < 300;
    }

    public static class Api
    {
        public const string BaseUrl = "https://www.spritefusion.com";

        public static readonly TimeSpan JsonTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(600);
        public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

        static readonly HttpClient Client = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        public static async Task<ApiResponse> PostJson(
            string path, string jsonBody, string bearerToken, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json")
            };

            request.Headers.Accept.ParseAdd(path == "/api/v1/generate"
                ? "text/event-stream"
                : "application/json");

            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            try
            {
                using var response = await Client.SendAsync(request, cts.Token);
                var body = await response.Content.ReadAsStringAsync();
                return new ApiResponse((int)response.StatusCode, body);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SpriteFusion request failed ({path}): {e.Message}");
                return new ApiResponse(0, "");
            }
        }

        public static async Task<ApiResponse> GetBytes(
            string url, string bearerToken, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            try
            {
                using var response = await Client.SendAsync(request, cts.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                return new ApiResponse((int)response.StatusCode, "", bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SpriteFusion download failed: {e.Message}");
                return new ApiResponse(0, "");
            }
        }

        public static List<GenerationEvent> ParseEvents(string streamText)
        {
            var events = new List<GenerationEvent>();
            var normalized = (streamText ?? "").Replace("\r\n", "\n");
            foreach (var rawEvent in normalized.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var dataLines = new List<string>();
                foreach (var line in rawEvent.Split('\n'))
                    if (line.StartsWith("data:"))
                        dataLines.Add(line.Substring(5).Trim());

                if (dataLines.Count == 0) continue;

                try
                {
                    var parsed = JsonUtility.FromJson<GenerationEvent>(string.Join("\n", dataLines));
                    if (parsed != null) events.Add(parsed);
                }
                catch
                {
                }
            }

            return events;
        }
    }
}
