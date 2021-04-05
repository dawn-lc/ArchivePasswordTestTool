using ArchivePasswordTestTool;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;

namespace module.dawnlc.me
{
    class Upgrade
    {
        /// <summary>
        /// 对比版本号
        /// </summary>
        /// <param name="sourceVersion">源版本</param>
        /// <param name="targetVersion">目标版本</param>
        /// <returns>true 目标版本较高, false 源版本较高 或 两者相等</returns>
        /// <exception cref="ArgumentException"></exception>
        private static bool ComparisonVersion(int[] sourceVersion, int[] targetVersion)
        {
            if (sourceVersion.Length == targetVersion.Length)
            {
                for (int i = 0; i < sourceVersion.Length; i++)
                {
                    if (sourceVersion[i] < targetVersion[i])
                    {
                        return true;
                    }
                }
                return false;
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
        /// <returns>true 目标版本类型较高, false 源版本类型较高 或 两者版本类型一致</returns>
        /// <exception cref="ArgumentException"></exception>
        private static bool ComparisonVersionType(string sourceVersionType, string targetVersionType)
        {
            List<List<string>> VersionType = new List<List<string>>
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
                    if (VersionType[i].Contains(sourceVersionType))
                    {
                        sourceVersionTypeLevel = VersionType.Count - i;
                    }
                    if (VersionType[i].Contains(targetVersionType))
                    {
                        targetVersionLevel = VersionType.Count - i;
                    }
                }

                if (sourceVersionTypeLevel < targetVersionLevel)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                throw new ArgumentException("版本类型无法识别");
            }
        }
        public static bool CheckUpgrade(Uri uri, Http.Method method, Dictionary<string, string> headers)
        {
            try
            {
                using (Http ReleasesLatestInfoData = new Http(uri, method, Http.CreateHeaders(headers), 5000))
                {
                    if (ReleasesLatestInfoData.GetResponseStatusCode() == HttpStatusCode.OK)
                    {
                        JObject ReleasesLatestInfo = (JObject)JsonConvert.DeserializeObject(ReleasesLatestInfoData.GetResponseString());
                        List<int> LatestVersion = new List<int>();
                        for (int i = 0; i < ReleasesLatestInfo["tag_name"].ToString().Split('-')[0].Split('.').Length; i++)
                        {
                            LatestVersion.Add(Convert.ToInt32(ReleasesLatestInfo["tag_name"].ToString().Split('-')[0].Split('.')[i]));
                        }
                        string LatestVersionType = null;
                        if (ReleasesLatestInfo["tag_name"].ToString().Split('-').Length > 1)
                        {
                            LatestVersionType = ReleasesLatestInfo["tag_name"].ToString().Split('-')[1];
                        }

                        if (!ComparisonVersionType(ProgramParameter.VersionType, LatestVersionType))
                        {
                            if (!ComparisonVersion(ProgramParameter.Version, LatestVersion.ToArray()))
                            {
                                return true;
                            }
                        }
                        Console.WriteLine("当前版本不是最新的，请前往下载最新版本。");
                        Process.Start(ProgramParameter.AppHomePage);
                        return false;
                    }
                    else
                    {
                        Console.WriteLine("检查更新失败！请检查您的网络情况。");
                        Process.Start(ProgramParameter.AppHomePage);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("检查更新失败！请检查您的网络情况。");
                Console.WriteLine(ex.ToString());
                Process.Start(ProgramParameter.AppHomePage);
                return false;
            }

        }
    }
}
