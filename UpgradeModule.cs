using ArchivePasswordTestTool;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;

namespace module.dawnlc.me
{
    class Upgrade
    {
        /// <summary>
        /// 对比版本号
        /// </summary>
        /// <param name="sourceVersion">源版本</param>
        /// <param name="targetVersion">目标版本</param>
        /// <returns> 1 目标版本较高, 0 两者版本一致, -1 源版本较高</returns>
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
        /// <returns> 1 目标版本类型较高, 0 两者版本类型一致, -1 源版本类型较高</returns>
        /// <exception cref="ArgumentException"></exception>
        private static int ComparisonVersionType(string sourceVersionType, string targetVersionType)
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

                        string LatestVersionType = null;

                        try
                        {
                            List<string> LatestVersionData = new List<string>(ReleasesLatestInfo["tag_name"].ToString().Split('-'));
                            LatestVersionType = LatestVersionData[1];
                            foreach (var item in LatestVersionData[0].Split('.'))
                            {
                                LatestVersion.Add(Convert.ToInt32(item));
                            }
                        }
                        catch (Exception)
                        {
                            Console.WriteLine("检查更新失败！请联系开发者进行修复。");
                            Process.Start(ProgramParameter.AppHomePage);
                            throw new Exception("错误的版本信息格式! \r\n" + ReleasesLatestInfo.ToString());
                        }

                        switch (ComparisonVersion(ProgramParameter.Version, LatestVersion.ToArray()))
                        {
                            case 1:
                                break;
                            case 0:
                                switch (ComparisonVersionType(ProgramParameter.VersionType, LatestVersionType))
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

                        Console.WriteLine("当前版本不是最新的，请前往下载最新版本。");
                        Process.Start(ProgramParameter.AppHomePage);
                        return false;
                    }
                    else
                    {
                        Console.WriteLine("检查更新失败！请检查您的网络情况。");
                        Process.Start(ProgramParameter.AppHomePage);
                        throw new Exception("检查更新失败！\r\n" + ReleasesLatestInfoData.GetResponseStatusCode() + "\r\n" + ReleasesLatestInfoData.GetResponseString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("检查更新失败！请检查您的网络情况。");
                Console.WriteLine(ex.ToString());
                Process.Start(ProgramParameter.AppHomePage);
                throw new Exception("检查更新失败！\r\n" + ex.ToString());
            }

        }
    }
}
