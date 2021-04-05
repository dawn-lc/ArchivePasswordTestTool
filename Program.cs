using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using module.dawnlc.me;
using System.Threading.Tasks;

namespace ArchivePasswordTestTool
{
    public class ProgramParameter
    {
        public static readonly string AppName = Assembly.GetExecutingAssembly().FullName.Substring(0, Assembly.GetExecutingAssembly().FullName.IndexOf(","));
        public static readonly string AppPath = Environment.CurrentDirectory + "\\";
        public static readonly int[] Version = new int[] { 1, 0, 7 };
        public static readonly string VersionType = "Preview";
        public static readonly string AppHomePage = "https://www.bilibili.com/read/cv6101558";
        public static readonly string Developer = "dawn-lc";
        public static bool DebugMode { get; set; }
        public static bool FastDebugMode { get; set; }
        public static FileInfo ArchiveDecryptionProgram { get; set; }
        public static FileInfo ArchiveFile { get; set; }
        public static FileInfo Dictionary { get; set; }
        public static string EncryptArchivePassword { get; set; }
        public static int DecryptArchiveThreadCount { get; set; }

        public ProgramParameter()
        {
            DebugMode = false;
            FastDebugMode = false;

            try
            {
                ArchiveDecryptionProgram = new FileInfo(AppPath + "7z.exe");
                if (!ArchiveDecryptionProgram.Exists)
                {
                    IO.ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe", AppPath + "7z.exe");
                }
                else
                {
                    using (FileStream data = new FileStream(AppPath + "7z.exe", FileMode.Open, FileAccess.Read))
                    {
                        if (!IO.ComparisonFile(Assembly.GetExecutingAssembly().GetManifestResourceStream(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe"), data))
                        {
                            while (true)
                            {
                                Console.WriteLine("注意！校验到7Zip与本程序自带的不一致，可能会导致程序无法正常工作。");
                                Console.Write("是否使用本程序自带的7Zip版本将其覆盖？(按Y覆盖/按N不覆盖): ");
                                switch (Console.ReadKey().Key)
                                {
                                    case ConsoleKey.Y:
                                        Console.Clear();
                                        IO.ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe", AppPath + "7z.exe");
                                        break;
                                    case ConsoleKey.N:
                                        break;
                                    default:
                                        Console.WriteLine();
                                        Console.WriteLine("输入错误!");
                                        continue;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                Console.WriteLine("释放7Zip时出现错误!(按任意键退出程序)");
                Console.ReadKey();
                Environment.Exit(0);
            }
            EncryptArchivePassword = null;
            DecryptArchiveThreadCount = 1;
        }

    }


    class Program
    {
        /// <summary>
        /// 程序集解析
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string[] assemblyName = Regex.Split(args.Name, ",", RegexOptions.IgnoreCase);
            try
            {
                if (!assemblyName[0].Contains(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType.Namespace))
                {
                    switch (assemblyName[0])
                    {
                        default:
                            return Assembly.LoadFrom(ProgramParameter.AppPath + assemblyName[0] + ".dll");
                    }
                }
                return null;
            }
            catch (System.IO.FileNotFoundException)
            {
                System.IO.Stream sm = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources." + assemblyName[0] + ".dll");
                byte[] bytes = new byte[sm.Length];
                sm.Read(bytes, 0, bytes.Length);
                sm.Seek(0, System.IO.SeekOrigin.Begin);
                return Assembly.Load(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public class Parameter
        {
            public Parameter(int Thread, string PassWord)
            {
                this.PassWord = PassWord;
                this.Thread = Thread;
            }

            public string PassWord { get; }
            public int Thread { get; }
        }
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            new ProgramParameter();

            if (Initialize(args))
            {
                if (ProgramParameter.ArchiveFile.Extension.ToLower().Contains("rar"))
                {
                    Console.WriteLine("RAR格式压缩包！由于RAR专利问题需要调用完整版7Zip！");
                    Console.WriteLine("请确保已安装完整版7zip！(按任意键继续！)");
                    Console.ReadKey();
                    if (string.IsNullOrEmpty(ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString()))
                    {
                        Console.WriteLine("调用完全体7Zip失败,请检查7Zip安装情况!(按任意键退出程序)");
                        Console.ReadKey();
                        Process.Start("https://sparanoid.com/lab/7z/");
                        return;
                    }
                    else
                    {
                        ProgramParameter.ArchiveDecryptionProgram = new FileInfo(ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString() + "7z.exe");
                    }
                }
                Dictionary<string, List<string>> returnData = RunProgram(ProgramParameter.ArchiveDecryptionProgram, new string[] { "t", "\"" + ProgramParameter.ArchiveFile.FullName + "\"", "-p" });
                if (!returnData.TryGetValue("Output", out List<string> Output))
                {
                    Console.WriteLine("压缩包损坏 或 不是支持的压缩包！（按任意键退出）");
                    Console.ReadKey();
                    return;
                }
                else
                {
                    if (Output.Where(p => p != null && p.Contains("Everything is Ok")).Any())
                    {
                        Console.WriteLine("非加密压缩包！（按任意键退出）");
                        Console.ReadKey();
                        return;
                    }
                }
                List<Parameter> Dictionary = new List<Parameter>();
                try
                {
                    string[] NewLine = new string[] { Environment.NewLine, "\r\n", "\n", "\r" };
                    using (StreamReader sr = new StreamReader(ProgramParameter.Dictionary.FullName, Encoding.UTF8))
                    {
                        string[] DictionaryData = sr.ReadToEnd().Split(NewLine, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                        Random r = new Random();
                        foreach (var item in DictionaryData)
                        {
                            Dictionary.Add(new Parameter(r.Next(0, ProgramParameter.DecryptArchiveThreadCount), item));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("尝试读取密码字典时出现错误!(按任意键退出程序)");
                    if (ProgramParameter.DebugMode) { Console.Write(ex.ToString()); }
                    Console.ReadKey();
                    return;
                }
                Stopwatch sw = new Stopwatch();
                sw.Restart();

                using (ConsoleExpand ConsoleCanvas = new ConsoleExpand())
                {
                    Parallel.ForEach(Dictionary, new ParallelOptions() { MaxDegreeOfParallelism = ProgramParameter.DecryptArchiveThreadCount }, (i, loopState) => {
                        ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 测试中...");
                        returnData = RunProgram(ProgramParameter.ArchiveDecryptionProgram, new string[] { "t", "\"" + ProgramParameter.ArchiveFile.FullName + "\"", "-p\"" + i.PassWord + "\"" });
                        if (returnData.ContainsKey("Output"))
                        {
                            if (returnData["Output"].Where(p => p != null && p.Contains("Everything is Ok")).Any())
                            {
                                ProgramParameter.EncryptArchivePassword = i.PassWord;
                                loopState.Stop();
                                ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 正确!");
                            }
                            else
                            {
                                ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 错误!");
                            }
                        }

                    });
                }
                sw.Stop();
                Console.SetCursorPosition(0, ProgramParameter.DecryptArchiveThreadCount+1);
                if (ProgramParameter.EncryptArchivePassword != null)
                {
                    Console.WriteLine("\r\n已找到解压密码: \r\n" + ProgramParameter.EncryptArchivePassword + "\r\n共耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                }
                else
                {
                    Console.WriteLine("已测试 [" + Dictionary.Count + "] 个密码, 没有找到正确的解压密码. 耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                }
                Console.Write("是否保存测试结果?(按回车键保存并退出/按其他任意键不保存并退出)");
                switch (Console.ReadKey().Key)
                {
                    case ConsoleKey.Enter:
                        using (StreamWriter file = new StreamWriter(ProgramParameter.AppPath + Path.GetFileName(ProgramParameter.ArchiveFile.Name) + "[测试报告].txt", false))
                        {
                            file.WriteLine("加密压缩包: " + ProgramParameter.ArchiveFile);
                            file.WriteLine("字典: " + ProgramParameter.Dictionary);
                            if (ProgramParameter.EncryptArchivePassword != null)
                            {
                                file.WriteLine("解压密码: " + ProgramParameter.EncryptArchivePassword);
                            }
                            else
                            {
                                file.WriteLine("没有找到正确的解压密码！");
                            }
                            file.Write("耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                            file.Close();
                        }
                        Process.Start("Explorer.exe", "/select, \"" + ProgramParameter.AppPath + Path.GetFileName(ProgramParameter.ArchiveFile.Name) + "[测试报告].txt" + "\"");
                        break;
                    default:
                        break;
                }
                return;
            }
            else
            {
                Console.WriteLine("初始化失败!");
                return;
            }

        }


        /// <summary>
        /// 判断键值是否存在
        /// </summary>
        /// <param name="key">键</param>
        /// <returns>存在返回true,不存在返回false</returns>
        public static bool IsExsits(RegistryKey key)
        {
            if (key == null)
            {
                return false;
            }
            else
            {
                return true;
            }
        }



        /// <summary>
        /// 读取注册表值
        /// </summary>
        /// <param name="Key">键</param>
        /// <param name="Value">值</param>
        /// <returns>如果Registry.LocalMachine中不包含Key或Key.Value不存在,返回null. 如果Registry.LocalMachine中包含Key且Key.Value存在,返回Key.Value.</returns>
        private static object ReadRegeditValue(string Key, string Value)
        {
            if (IsExsits(Registry.LocalMachine.OpenSubKey(Key)))
            {
                return Registry.LocalMachine.OpenSubKey(Key).GetValue(Value);
            }
            else
            {
                return null;
            }
        }

        private static bool StartupParametersCheck(List<string> StartupParameters, string ParameterFlag)
        {
            if (StartupParameters.Contains(ParameterFlag))
            {
                try
                {
                    if (string.IsNullOrEmpty(StartupParameters[StartupParameters.IndexOf(ParameterFlag) + 1]) || StartupParameters[StartupParameters.IndexOf(ParameterFlag) + 1].Substring(0, 1) == "-")
                    {
                        throw new Exception();
                    }
                    else
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.Write("启动参数存在错误！请检查参数：[" + ParameterFlag + "]");
                    Console.ReadLine();
                    if (ProgramParameter.DebugMode) { Console.Write(ex.ToString()); }
                    return false;
                }
            }
            return false;
        }

        private static bool Initialize(string[] args)
        {
            Console.Title = "压缩包密码测试工具";
            List<string> StartupParameters = args.ToList();

            if (StartupParameters.Contains("-Debug"))
            {
                while (true)
                {
                    Console.Write("注意。程序处于Debug状态，将会在控制台输出大量信息，是否继续？(按Y继续/按N退出Debug模式): ");
                    switch (Console.ReadKey().Key)
                    {
                        case ConsoleKey.Y:
                            ProgramParameter.DebugMode = true;
                            Debugger.Launch();
                            break;
                        case ConsoleKey.N:
                            break;
                        case ConsoleKey.F:
                            ProgramParameter.DebugMode = true;
                            ProgramParameter.FastDebugMode = true;
                            Debugger.Launch();
                            break;
                        default:
                            Console.WriteLine();
                            Console.WriteLine("输入错误!");
                            continue;
                    }
                    Console.Clear();
                    break;
                }
            }

            if (Debugger.IsAttached)
            {
                Console.Title += " - DEBUG ";
                if (ProgramParameter.FastDebugMode)
                {
                    Console.Title += "[Fast]";
                }
            }

            using (FileStream configFile = new FileStream(ProgramParameter.AppPath + "config.json", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                JObject config = new JObject();
                if (configFile.Length == 0)
                {
                    config.Add("CheckUpgrade", DateTime.MinValue);
                    configFile.Write(Encoding.UTF8.GetBytes(config.ToString()), 0, Encoding.UTF8.GetBytes(config.ToString()).Length);
                    configFile.Flush();
                    configFile.Position = 0;
                }
                using (StreamReader configString = new StreamReader(configFile))
                {

                    config = (JObject)JsonConvert.DeserializeObject(configString.ReadToEnd());
                    Console.WriteLine("上次检查更新: " + config["CheckUpgrade"].ToObject<DateTime>().ToLocalTime().ToString());
                    if (config["CheckUpgrade"].ToObject<DateTime>() < (DateTime.Now - new TimeSpan(1, 0, 0)))
                    {
                        Console.WriteLine("正在检查更新...");
                        if (Upgrade.CheckUpgrade(new Uri("https://api.github.com/repos/" + ProgramParameter.Developer + "/" + ProgramParameter.AppName + "/releases/latest"), Http.Method.GET, new Dictionary<string, string>() { ["user-agent"] = ProgramParameter.AppName + " " + string.Join(".", ProgramParameter.Version) + ";" }))
                        {
                            Console.WriteLine("当前本地程序已是最新版本。");
                            config["CheckUpgrade"] = DateTime.Now;
                        }
                        else
                        {
                            Console.ReadLine();
                            return false;
                        }
                    }
                    configFile.Seek(0, SeekOrigin.Begin);
                    configFile.SetLength(0);
                    configFile.Write(Encoding.UTF8.GetBytes(config.ToString()), 0, Encoding.UTF8.GetBytes(config.ToString()).Length);
                    configFile.Flush();
                }
            }


            if (StartupParametersCheck(StartupParameters, "-F"))
            {
                try
                {
                    ProgramParameter.ArchiveFile = new FileInfo(Path.GetFullPath(StartupParameters[StartupParameters.IndexOf("-F") + 1]));
                    while (!ProgramParameter.ArchiveFile.Exists)
                    {
                        Console.WriteLine("没有找到您的压缩包[" + ProgramParameter.ArchiveFile.FullName + "]!");
                        Console.WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        ProgramParameter.ArchiveFile = new FileInfo(Console.ReadLine());
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("尝试读取压缩包信息时出现错误!(按任意键退出程序)");
                    Console.ReadKey();
                    return false;

                }
            }
            else
            {
                do
                {
                    Console.WriteLine("您似乎没有提供需要进行测试的压缩包地址!");
                    Console.WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                    ProgramParameter.ArchiveFile = new FileInfo(Console.ReadLine());
                } while (!ProgramParameter.ArchiveFile.Exists);
            }


            if (StartupParametersCheck(StartupParameters, "-D"))
            {
                try
                {
                    ProgramParameter.Dictionary = new FileInfo(Path.GetFullPath(StartupParameters[StartupParameters.IndexOf("-D") + 1]));
                    while (!ProgramParameter.Dictionary.Exists)
                    {
                        Console.WriteLine("没有找到您的密码字典[" + ProgramParameter.Dictionary.FullName + "]!");
                        Console.WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        ProgramParameter.Dictionary = new FileInfo(Console.ReadLine());
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("尝试读取密码字典时出现错误!(按任意键退出程序)");
                    Console.ReadKey();
                    return false;

                }
            }
            else
            {
                do
                {
                    Console.WriteLine("您似乎没有提供您的密码字典地址!");
                    Console.WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                    ProgramParameter.Dictionary = new FileInfo(Console.ReadLine());
                } while (!ProgramParameter.Dictionary.Exists);
            }


            if (StartupParametersCheck(StartupParameters, "-T"))
            {
                try
                {
                    ProgramParameter.DecryptArchiveThreadCount = Convert.ToInt32(StartupParameters[StartupParameters.IndexOf("-T") + 1]);
                }
                catch (Exception ex)
                {
                    Console.Write("启动参数存在错误！请检查参数：[-T]");
                    Console.ReadLine();
                    if (ProgramParameter.DebugMode) { Console.Write(ex.ToString()); }
                    return false;
                }
                if (ProgramParameter.DecryptArchiveThreadCount > (Environment.ProcessorCount - 1))
                {
                    Console.WriteLine("测试密码线程数过高! (已调整为最大线程[" + (Environment.ProcessorCount - 1).ToString() + "])");
                    ProgramParameter.DecryptArchiveThreadCount = Environment.ProcessorCount - 1;
                }
            }
            else
            {
                Console.WriteLine("您似乎没有提供进行测试的线程数！");
                do
                {
                    string DecryptArchiveThreadCountTemp;
                    do
                    {
                        Console.Write("请输入测试密码使用的线程数(操作完成后, 按回车键继续):");
                        DecryptArchiveThreadCountTemp = Console.ReadLine();
                    } while (string.IsNullOrEmpty(DecryptArchiveThreadCountTemp));
                    try
                    {
                        ProgramParameter.DecryptArchiveThreadCount = Convert.ToInt32(DecryptArchiveThreadCountTemp);
                        break;
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("线程数存在错误，请检查输入的参数！");
                        continue;
                    }
                } while (true);
                if (ProgramParameter.DecryptArchiveThreadCount > (Environment.ProcessorCount - 1))
                {
                    Console.WriteLine("进行测试线程数过高! 已调整为最大线程(" + (Environment.ProcessorCount - 1).ToString() + ")");
                    ProgramParameter.DecryptArchiveThreadCount = Environment.ProcessorCount - 1;
                }
            }
            return true;
        }
        public static void ExtractResFile(string resFileName, string outputFile)
        {
            BufferedStream inStream = null;
            FileStream outStream = null;
            try
            {
                inStream = new BufferedStream(Assembly.GetExecutingAssembly().GetManifestResourceStream(resFileName));
                outStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                byte[] buffer = new byte[1024];
                int length;
                while ((length = inStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    outStream.Write(buffer, 0, length);
                }
                outStream.Flush();
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return;
            }
            finally
            {
                outStream?.Close();
                inStream?.Close();
            }
        }
        static Dictionary<string, List<string>> RunProgram(FileInfo Program, string[] Arguments)
        {
            using (Process p = new Process())
            {
                List<string> Error = new List<string>();
                List<string> Output = new List<string>();
                void StoreError(object o, DataReceivedEventArgs e)
                {
                    Error.Add(e.Data);
                }
                void StoreOutput(object o, DataReceivedEventArgs e)
                {
                    Output.Add(e.Data);
                }
                try
                {
                    p.StartInfo.FileName = Program.FullName;
                    p.StartInfo.Arguments = string.Join(" ", Arguments);
                    p.StartInfo.CreateNoWindow = true;

                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardInput = true;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;

                    p.OutputDataReceived += StoreOutput;
                    p.ErrorDataReceived += StoreError;

                    p.Start();

                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();

                    using (StreamWriter sr = p.StandardInput)
                    {
                        p.StandardInput.AutoFlush = true;
                        sr.WriteLine();
                    }

                    p.WaitForExit();

                    if (ProgramParameter.DebugMode && !ProgramParameter.FastDebugMode)
                    {
                        Console.Write(string.Join(Environment.NewLine, Output.ToArray()));
                        Console.Write(string.Join(Environment.NewLine, Error.ToArray()));
                    }
                    return new Dictionary<string, List<string>> { ["Output"] = Output, ["Error"] = Error };
                }
                catch (Exception ex)
                {
                    if (ProgramParameter.DebugMode && !ProgramParameter.FastDebugMode)
                    {
                        Console.Write(string.Join(Environment.NewLine, Output.ToArray()));
                        Console.Write(string.Join(Environment.NewLine, Error.ToArray()));
                        Console.WriteLine(ex.ToString());
                    }
                    return new Dictionary<string, List<string>> { ["RunTimeError"] = new List<string> { ex.ToString() } };
                }
                finally
                {
                    p.OutputDataReceived -= StoreOutput;
                    p.ErrorDataReceived -= StoreError;
                    p.Close();
                }
            }
        }
    }
}
