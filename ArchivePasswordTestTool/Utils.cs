using Spectre.Console;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArchivePasswordTestTool
{
    internal class Utils
    {
        public static class Util
        {
            private static readonly SHA256 HASH = SHA256.Create();

            /// <summary>
            /// 计算文件Hash
            /// </summary>
            /// <param name="File">文件流</param>
            /// <returns>Hash</returns>
            public static byte[] FileHash(Stream File)
            {
                return HASH.ComputeHash(File);
            }

            /// <summary>
            /// 根据文件头判断压缩包格式
            /// </summary>
            /// <param name="filePath">压缩包文件路径</param>
            /// <returns>压缩包格式（7z、Zip、Rar、Rar5），如果无法识别则返回null</returns>
            public static string? DetectArchiveFormatByHeader(string filePath)
            {
                try
                {
                    using var fileStream = File.OpenRead(filePath);
                    byte[] header = new byte[32]; // 读取32字节应该足够识别常见格式
                    int bytesRead = fileStream.Read(header, 0, header.Length);

                    if (bytesRead < 8) // 至少需要8字节来识别常见格式
                        return null;

                    // 检查7z格式签名：37 7A BC AF 27 1C
                    if (bytesRead >= 6 &&
                        header[0] == 0x37 && header[1] == 0x7A &&
                        header[2] == 0xBC && header[3] == 0xAF &&
                        header[4] == 0x27 && header[5] == 0x1C)
                    {
                        return "7z";
                    }

                    // 检查Zip格式签名：50 4B 03 04 或 50 4B 05 06 或 50 4B 07 08
                    if (bytesRead >= 4 &&
                        header[0] == 0x50 && header[1] == 0x4B)
                    {
                        if (header[2] == 0x03 && header[3] == 0x04) // 标准Zip
                            return "Zip";
                        if (header[2] == 0x05 && header[3] == 0x06) // 空Zip
                            return "Zip";
                        if (header[2] == 0x07 && header[3] == 0x08) // 分卷Zip
                            return "Zip";
                    }

                    // 检查Rar4格式签名：52 61 72 21 1A 07 00
                    if (bytesRead >= 7 &&
                        header[0] == 0x52 && header[1] == 0x61 &&
                        header[2] == 0x72 && header[3] == 0x21 &&
                        header[4] == 0x1A && header[5] == 0x07 &&
                        header[6] == 0x00)
                    {
                        return "Rar";
                    }

                    // 检查Rar5格式签名：52 61 72 21 1A 07 01 00
                    if (bytesRead >= 8 &&
                        header[0] == 0x52 && header[1] == 0x61 &&
                        header[2] == 0x72 && header[3] == 0x21 &&
                        header[4] == 0x1A && header[5] == 0x07 &&
                        header[6] == 0x01 && header[7] == 0x00)
                    {
                        return "Rar5";
                    }

                    return null;
                }
                catch (Exception)
                {
                    return null;
                }
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
                try
                {
                    if (!Parameters.Contains($"-{Flag}"))
                    {
                        throw new ArgumentException(Flag);
                    }
                    if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                    {
                        throw new ArgumentException(Flag);
                    }
                    return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
                }
                catch (Exception)
                {
                    return DefaultParameter;
                }

            }
            public static T GetParameter<T>(string[] Parameters, string Flag, T DefaultParameter)
            {
                return GetParameter(new List<string>(Parameters), Flag, DefaultParameter);
            }

            public static T? GetParameter<T>(List<string> Parameters, string Flag)
            {
                try
                {
                    if (!Parameters.Contains($"-{Flag}"))
                    {
                        throw new ArgumentException(Flag);
                    }
                    if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                    {
                        throw new ArgumentException(Flag);
                    }
                    return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
                }
                catch (Exception)
                {
                    return default;
                }
            }

            public static T? GetParameter<T>(string[] Parameters, string Flag)
            {
                return GetParameter<T>(new List<string>(Parameters), Flag);
            }

        }
        public class Dictionary
        {
            private int Limit { get; init; }
            private int CurrentLine { get; set; }
            public int Count { get; init; }
            public string DictionaryPath { get; init; }
            private bool S { get; set; }
            private ConcurrentQueue<string> AKeys { get; set; }
            private ConcurrentQueue<string> BKeys { get; set; }
            public Dictionary(string dictionaryPath, int limit = 2048)
            {
                DictionaryPath = dictionaryPath;
                Limit = limit;
                CurrentLine = 0;
                Count = File.ReadLines(DictionaryPath, Encoding.UTF8).Count();
                AKeys = new ConcurrentQueue<string>(ReadLines());
                BKeys = new ConcurrentQueue<string>(ReadLines());
                S = true;
            }
            private IEnumerable<string> ReadLines()
            {
                var lines = File.ReadLines(DictionaryPath, Encoding.UTF8).Skip(CurrentLine).Take(Limit);
                CurrentLine += lines.Count();
                return lines;
            }
            public string Pop()
            {
                if (S)
                {
                    if (AKeys.IsEmpty)
                    {
                        S = false;
                        AKeys = new ConcurrentQueue<string>(ReadLines());
                    }
                    else
                    {
                        if (AKeys.TryDequeue(out string? key))
                        {
                            return key;
                        }
                    }
                }
                else
                {
                    if (BKeys.IsEmpty)
                    {
                        S = true;
                        BKeys = new ConcurrentQueue<string>(ReadLines());
                    }
                    else
                    {
                        if (BKeys.TryDequeue(out string? key))
                        {
                            return key;
                        }
                    }
                }
                return Pop();
            }
        }
    }
}
