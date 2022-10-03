using Spectre.Console;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ArchivePasswordTestTool.Utils.Util;

namespace ArchivePasswordTestTool
{
    internal class Utils
    {
        public static class Util
        {
            /// <summary>
            /// 比较两个字节数组是否相等
            /// </summary>
            /// <param name="A">byte数组1</param>
            /// <param name="B">byte数组2</param>
            /// <returns>是否相等</returns>
            public static bool Equals(byte[] A, byte[] B)
            {
                if (A == null || B == null) return false;
                if (A.Length != B.Length) return false;
                for (int i = 0; i < A.Length; i++)
                    if (A[i] != B[i])
                        return false;
                return true;
            }

            /// <summary>
            /// 计算文件Hash
            /// </summary>
            /// <param name="File">文件流</param>
            /// <returns>Hash</returns>
            public static byte[] FileHash(Stream File)
            {
                using MD5 FileMD5 = MD5.Create();
                return FileMD5.ComputeHash(File);
            }
            /// <summary>
            /// 比较文件Hash是否一致
            /// </summary>
            /// <param name="File">文件流</param>
            /// <param name="Hash">文件流</param>
            /// <returns>是否一致</returns>
            public static bool ComparisonFileHash(Stream File, byte[] Hash)
            {
                try
                {
                    using MD5 FileMD5 = MD5.Create();
                    return Equals(FileMD5.ComputeHash(File), Hash);
                }
                catch (Exception ex)
                {
                    Error(ex.ToString());
                    return false;
                }
            }
            /// <summary>
            /// 比较两个文件是否一致
            /// </summary>
            /// <param name="FileA">文件流</param>
            /// <param name="FileB">文件流</param>
            /// <returns>是否一致</returns>
            public static bool ComparisonFile(Stream FileA, Stream FileB)
            {
                try
                {
                    using MD5 FileMD5 = MD5.Create();
                    //Log(Convert.ToBase64String(FileMD5.ComputeHash(FileA)));
                    return Equals(FileMD5.ComputeHash(FileA), FileMD5.ComputeHash(FileB));
                }
                catch (Exception ex)
                {
                    Error(ex.ToString());
                    return false;
                }
            }
            /// <summary> 从JSON文件反序列化对象 </summary>
            public static async Task<T> DeserializeJSONFileAsync<T>(string path) where T : new()
            {
                T? data;
                return File.Exists(path) ? (data = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path))) != null ? data : new T() : new T();
            }
            /// <summary> 从JSON文件反序列化对象 </summary>
            public static T DeserializeJSONFile<T>(string path) where T : new()
            {
                T? data;
                return File.Exists(path) ? (data = JsonSerializer.Deserialize<T>(File.ReadAllText(path))) != null ? data : new T() : new T();
            }
            public static void Log(string value)
            {
                AnsiConsole.MarkupLine($"[bold][[{DateTime.Now}]] [lime]I[/][/] {value}");
            }
            public static void Warn(string value)
            {
                AnsiConsole.MarkupLine($"[bold][[{DateTime.Now}]] [orangered1]W[/][/] {value}");
            }
            public static void Error(string value)
            {
                AnsiConsole.MarkupLine($"[bold][[{DateTime.Now}]] [red]E[/][/] {value}");
            }
            public static bool StartupParametersCheck(List<string> Parameters, string Flag)
            {
                if (Parameters.Contains($"-{Flag}"))
                {
                    try
                    {
                        return !string.IsNullOrEmpty(GetParameter(Parameters, Flag, ""));
                    }
                    catch (Exception)
                    {
                        throw new Exception($"Startup parameters error. Please check: {Flag}");
                    }
                }
                return false;
            }
            public static bool StartupParametersCheck(string[] Parameters, string Flag)
            {
                return StartupParametersCheck(new List<string>(Parameters), Flag);
            }
            public static T GetParameter<T>(List<string> Parameters, string Flag, T DefaultParameter)
            {
                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T)) ?? DefaultParameter;
            }
            public static T GetParameter<T>(string[] Parameters, string Flag, T DefaultParameter)
            {
                return GetParameter<T>(new List<string>(Parameters), Flag, DefaultParameter);
            }
            public static T? GetParameter<T>(List<string> Parameters, string Flag)
            {
                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
            }
            public static T? GetParameter<T>(string[] Parameters, string Flag)
            {
                return GetParameter<T>(new List<string>(Parameters), Flag);
            }

        }
        public static class Upgrade
        {
            public class ReleasesInfo
            {
                public class Asset
                {
                    [JsonPropertyName("name")]
                    public string Name { get; set; }
                    [JsonPropertyName("label")]
                    public string Label { get; set; }
                    [JsonPropertyName("content_type")]
                    public string ContentType { get; set; }
                    [JsonPropertyName("created_at")]
                    public string CreatedAt { get; set; }
                    [JsonPropertyName("updated_at")]
                    public string UpdatedAt { get; set; }
                    [JsonPropertyName("browser_download_url")]
                    public string DownloadUrl { get; set; }
                }

                [JsonPropertyName("tag_name")]
                public string TagName { get; set; }
                [JsonPropertyName("target_commitish")]
                public string TargetCommitish { get; set; }
                [JsonPropertyName("name")]
                public string Name { get; set; }
                [JsonPropertyName("prerelease")]
                public bool Prerelease { get; set; }
                [JsonPropertyName("created_at")]
                public string CreatedAt { get; set; }
                [JsonPropertyName("published_at")]
                public string PublishedAt { get; set; }
                [JsonPropertyName("assets")]
                public List<Asset> Assets { get; set; }
                [JsonPropertyName("body")]
                public string? Body { get; set; }
            }
            /// <summary>
            /// 对比版本号
            /// </summary>
            /// <param name="sourceVersion">源版本</param>
            /// <param name="targetVersion">目标版本</param>
            /// <returns><see langword="1"/> 目标版本类型较高<br /><see langword="0"/> 两者版本类型一致<br /><see langword="-1"/> 源版本类型较高</returns> 
            /// <exception cref="ArgumentException"></exception>
            private static int ComparisonVersion(int[] sourceVersion, int[] targetVersion)
            {
                if (sourceVersion.Length == targetVersion.Length)
                {
                    for (int i = 0; i < sourceVersion.Length; i++)
                    {
                        if (sourceVersion[i] != targetVersion[i])
                        {
                            if (sourceVersion[i] < targetVersion[i])
                            {
                                return 1;
                            }
                            else
                            {
                                return -1;
                            }
                        }
                    }
                    return 0;
                }
                else
                {
                    throw new ArgumentException("版本号格式不一致");
                }

            }
            /// <summary>
            /// 对比版本类型
            /// </summary>
            /// <param name="sourceVersion">源版本类型</param>
            /// <param name="targetVersion">目标版本类型</param>
            /// <returns><see langword="1"/> 目标版本类型较高<br /><see langword="0"/> 两者版本类型一致<br /><see langword="-1"/> 源版本类型较高</returns> 
            /// <exception cref="ArgumentException"></exception>
            private static int ComparisonVersionType(string sourceVersionType, string targetVersionType)
            {
                List<List<string>> VersionType = new()
                {
                    new List<string> { "fixpush" },
                    new List<string> { "final", "full version", "enhance", "standard" },
                    new List<string> { "release", "release candidate" },
                    new List<string> { "preview" },
                    new List<string> { "beta" },
                    new List<string> { "alpha" },
                    new List<string> { "free", "demo", "test" }
                };

                if (VersionType.Where(p => p.Contains(sourceVersionType.ToLower())).Any() && VersionType.Where(p => p.Contains(targetVersionType.ToLower())).Any())
                {
                    int sourceVersionTypeLevel = VersionType.Count;
                    int targetVersionLevel = VersionType.Count;
                    for (int i = 1; i < VersionType.Count; i++)
                    {
                        if (VersionType[i].Contains(sourceVersionType.ToLower()))
                        {
                            sourceVersionTypeLevel = VersionType.Count - i;
                        }
                        if (VersionType[i].Contains(targetVersionType.ToLower()))
                        {
                            targetVersionLevel = VersionType.Count - i;
                        }
                    }

                    if (sourceVersionTypeLevel < targetVersionLevel)
                    {
                        return 1;
                    }
                    else
                    {
                        if (sourceVersionTypeLevel == targetVersionLevel)
                        {
                            return 0;
                        }
                        else
                        {
                            return -1;
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("版本类型无法识别");
                }
            }
            public static bool CheckUpgrade(ReleasesInfo LatestInfo, int[] version, string versionType)
            {
                try
                {
                    List<string> LatestVersionData = new(LatestInfo.TagName.Split('-'));
                    List<int> LatestVersion = new();
                    string LatestVersionType = LatestVersionData[1];
                    foreach (var item in LatestVersionData[0].Split('.'))
                    {
                        LatestVersion.Add(Convert.ToInt32(item));
                    }
                    switch (ComparisonVersion(version, LatestVersion.ToArray()))
                    {
                        case 1:
                            break;
                        case 0:
                            switch (ComparisonVersionType(versionType, LatestVersionType))
                            {
                                case 1:
                                    break;
                                case 0:
                                    return true;
                                case -1:
                                    return true;
                            }
                            break;
                        case -1:
                            return true;
                    }
                    return false;
                }
                catch (Exception)
                {
                    Error("检查更新失败！请联系开发者进行修复。");
                    throw new Exception("无法解析的版本信息格式! \r\n" + LatestInfo.ToString());
                }
            }
        }
        public static class HTTP
        {
            public static HttpRequestMessage CreateRequest(Uri url, HttpMethod method, HttpContent? content)
            {
                return new HttpRequestMessage()
                {
                    RequestUri = url,
                    Method = method,
                    Content = content,
                };
            }
            private static readonly TimeSpan DefaultTimeout = new(0, 0, 1, 0);
            public static HttpClient Constructor(Dictionary<string, IEnumerable<string>>? head, TimeSpan? timeout)
            {
                HttpClientHandler clientHandler = new() { 
                    AutomaticDecompression = DecompressionMethods.GZip
                };
                clientHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };
                HttpClient Client = new(clientHandler);
                Client.DefaultRequestHeaders.Clear();
                Client.Timeout = timeout ?? DefaultTimeout;
                foreach (KeyValuePair<string, IEnumerable<string>> item in head ?? new())
                {
                    Client.DefaultRequestHeaders.Add(item.Key, item.Value);
                }
                return Client;
            }
            public static HttpClient Constructor(Dictionary<string, string>? head = null, TimeSpan? timeout = null)
            {
                var heads = new Dictionary<string, IEnumerable<string>>();
                foreach (var item in head ?? new())
                {
                    heads.Add(item.Key, new List<string>(item.Value.Split(';')));
                }
                return Constructor(heads, timeout ?? DefaultTimeout);
            }
            public static async Task<HttpResponseMessage> GetAsync(Uri url, Dictionary<string, IEnumerable<string>>? head = null, TimeSpan? timeout = null)
            {
                return await Constructor(head, timeout).GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            }
            public static async Task<HttpResponseMessage> PostAsync(Uri url, HttpContent content, Dictionary<string, IEnumerable<string>>? head = null, TimeSpan? timeout = null)
            {
                return await Constructor(head, timeout).PostAsync(url, content);
            }
            public static async Task<HttpResponseMessage> PatchAsync(Uri url, HttpContent content, Dictionary<string, IEnumerable<string>>? head = null, TimeSpan? timeout = null)
            {
                return await Constructor(head, timeout).PatchAsync(url, content);
            }
            public static async Task<HttpResponseMessage> PutAsync(Uri url, HttpContent content, Dictionary<string, IEnumerable<string>>? head = null, TimeSpan? timeout = null)
            {
                return await Constructor(head, timeout).PutAsync(url, content);
            }
            public static async Task DownloadAsync(HttpResponseMessage response, string path, ProgressTask task, string? name)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    byte[] buffer = new byte[8192];
                    Stream ResponseStream = await response.Content.ReadAsStreamAsync();
                    using var destination = new FileStream(path, FileMode.OpenOrCreate);
                    long contentLength = response.Content.Headers.ContentLength ?? 0;
                    int bytesRead;
                    while ((bytesRead = await ResponseStream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                        task.Increment((double)bytesRead / contentLength * 100);
                    }
                    task.Increment(100);
                    task.StopTask();
                    Log($"{name} 下载完成");
                }
                catch (Exception ex)
                {
                    throw new($"Downloading {name ?? response.RequestMessage?.RequestUri?.ToString() ?? path} \r\n {ex}");
                }
            }
        }
    }
}
