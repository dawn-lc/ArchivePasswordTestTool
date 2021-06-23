using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using SharpRaven;
using SharpRaven.Data;
using Microsoft.Win32;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using module.dawnlc.me;
using System.Security.Cryptography;

namespace ArchivePasswordTestTool
{
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


    public class Main
    {
        static ProgramParameter programParameter { get; set; }
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

        public static void WriteLine(string s = null)
        {
            if (s == null)
            {
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("[" + DateTime.Now.ToLocalTime().ToString() +"] "+ s);
            }
        }
        public static void Write(string s = null)
        {
            if (s != null)
            {
                Console.Write("[" + DateTime.Now.ToLocalTime().ToString() + "] " + s);
            }
        }

        private static bool StartupParametersCheck(List<string> StartupParameters, string ParameterFlag)
        {
            if (StartupParameters.Contains(ParameterFlag))
            {
                try
                {
                    if (!(string.IsNullOrEmpty(StartupParameters[StartupParameters.IndexOf(ParameterFlag) + 1]) || StartupParameters[StartupParameters.IndexOf(ParameterFlag) + 1].Substring(0, 1) == "-"))
                    {
                        return true;
                    }
                    else
                    {
                        throw new Exception("启动参数存在错误！请检查参数：[" + ParameterFlag + "]");
                    }
                }
                catch (Exception ex)
                {
                    WriteLine("启动参数存在错误！请检查参数：[" + ParameterFlag + "]");
                    if (programParameter.DebugMode) { WriteLine(ex.ToString()); }
                    throw ex;
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
                    Write("注意。程序处于Debug状态，将会在控制台输出大量信息，是否继续？(按Y继续/按N退出Debug模式): ");
                    switch (Console.ReadKey().Key)
                    {
                        case ConsoleKey.Y:
                            programParameter.DebugMode = true;
                            Debugger.Launch();
                            break;
                        case ConsoleKey.N:
                            break;
                        case ConsoleKey.F:
                            programParameter.DebugMode = true;
                            programParameter.FastDebugMode = true;
                            Debugger.Launch();
                            break;
                        default:
                            WriteLine();
                            WriteLine("输入错误!");
                            continue;
                    }
                    Console.Clear();
                    break;
                }
            }

            if (Debugger.IsAttached || programParameter.DebugMode)
            {
                //programParameter.DebugMode = true;
                Console.Title += " - DEBUG ";
                if (programParameter.FastDebugMode)
                {
                    Console.Title += "[Fast]";
                }
            }

            using (FileStream configFile = new FileStream(programParameter.AppPath + "config.json", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                JObject config = new JObject();
                if (configFile.Length == 0)
                {
                    config.Add("CheckUpgrade", DateTime.MinValue);
                    configFile.Write(Encoding.UTF8.GetBytes(config.ToString()), 0, Encoding.UTF8.GetBytes(config.ToString()).Length);
                    configFile.Flush();
                    configFile.Seek(0, SeekOrigin.Begin);
                }
                using (StreamReader configString = new StreamReader(configFile,Encoding.UTF8))
                {
                    config = JsonConvert.DeserializeObject<JObject>(configString.ReadToEnd());
                    WriteLine("上次检查更新: " + config["CheckUpgrade"].ToObject<DateTime>().ToLocalTime().ToString());
                    if (config["CheckUpgrade"].ToObject<DateTime>() < (DateTime.Now - new TimeSpan(1, 0, 0)))
                    {
                        WriteLine("正在从github.com获取最新版本信息...");
                        if (Upgrade.CheckUpgrade(new Uri("https://api.github.com/repos/" + programParameter.Developer + "/" + programParameter.AppName + "/releases/latest"), Http.Method.GET, new Dictionary<string, string>() { ["user-agent"] = programParameter.AppName + " " + string.Join(".", programParameter.Version) + ";" },programParameter.Version,programParameter.VersionType))
                        {
                            WriteLine("当前本地程序已是最新版本。");
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

            try
            {
                if (StartupParametersCheck(StartupParameters, "-F"))
                {
                    programParameter.ArchiveFile = new FileInfo(Path.GetFullPath(StartupParameters[StartupParameters.IndexOf("-F") + 1]));
                    while (!programParameter.ArchiveFile.Exists)
                    {
                        WriteLine("没有找到您的压缩包[" + programParameter.ArchiveFile.FullName + "]!");
                        WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        programParameter.ArchiveFile = new FileInfo(Path.GetFullPath(Console.ReadLine().Replace("\"",null)));
                    }
                }
                else
                {
                    do
                    {
                        WriteLine("您似乎没有提供需要进行测试的压缩包地址!");
                        WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        programParameter.ArchiveFile = new FileInfo(Path.GetFullPath(Console.ReadLine().Replace("\"", null)));
                    } while (!programParameter.ArchiveFile.Exists);
                }
            }
            catch (Exception ex)
            {
                WriteLine("尝试读取压缩包信息时出现错误!");
                throw ex;
            }


            try
            {
                if (StartupParametersCheck(StartupParameters, "-D"))
                {

                    programParameter.Dictionary = new FileInfo(Path.GetFullPath(StartupParameters[StartupParameters.IndexOf("-D") + 1]));
                    while (!programParameter.Dictionary.Exists)
                    {
                        WriteLine("没有找到您的密码字典[" + programParameter.Dictionary.FullName + "]!");
                        WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        programParameter.Dictionary = new FileInfo(Path.GetFullPath(Console.ReadLine().Replace("\"", null)));
                    }
                }
                else
                {
                    WriteLine("您似乎没有提供您的密码字典地址");
                    WriteLine("是否使用默认字典?(Y[是]/N[否])");
                    switch (Console.ReadKey(true).Key)
                    {
                        case ConsoleKey.Y:
                            if (File.Exists(programParameter.AppPath + "PasswordDictionary.txt"))
                            {
                                programParameter.Dictionary = new FileInfo(Path.GetFullPath(programParameter.AppPath + "PasswordDictionary.txt"));
                            }
                            else
                            {
                                WriteLine("默认字典不存在,提供您的密码字典地址");
                                do
                                {
                                    WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                                    programParameter.Dictionary = new FileInfo(Path.GetFullPath(Console.ReadLine().Replace("\"", null)));
                                } while (!programParameter.Dictionary.Exists);
                            }
                            break;
                        case ConsoleKey.N:
                            do
                            {
                                WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                                programParameter.Dictionary = new FileInfo(Path.GetFullPath(Console.ReadLine().Replace("\"", null)));
                            } while (!programParameter.Dictionary.Exists);
                            break;
                        default:
                            break;
                    }
                    
                }
            }
            catch (Exception ex)
            {
                WriteLine("尝试读取密码字典时出现错误!");
                throw ex;
            }


            if (StartupParametersCheck(StartupParameters, "-T"))
            {
                try
                {
                    programParameter.DecryptArchiveThreadCount = Convert.ToInt32(StartupParameters[StartupParameters.IndexOf("-T") + 1]);
                }
                catch (Exception ex)
                {
                    Write("启动参数存在错误！请检查参数：[-T]");
                    if (programParameter.DebugMode) { WriteLine(ex.ToString()); }
                    throw ex;
                }
                if (programParameter.DecryptArchiveThreadCount > (Environment.ProcessorCount - 1))
                {
                    WriteLine("测试密码线程数过高! (已调整为最大线程[" + (Environment.ProcessorCount - 1).ToString() + "])");
                    programParameter.DecryptArchiveThreadCount = Environment.ProcessorCount - 1;
                }
            }
            else
            {
                WriteLine("您似乎没有提供进行测试的线程数！");
                do
                {
                    string DecryptArchiveThreadCountTemp;
                    do
                    {
                        Write("请输入测试密码使用的线程数(操作完成后, 按回车键继续):");
                        DecryptArchiveThreadCountTemp = Console.ReadLine();
                    } while (string.IsNullOrEmpty(DecryptArchiveThreadCountTemp));
                    try
                    {
                        programParameter.DecryptArchiveThreadCount = Convert.ToInt32(DecryptArchiveThreadCountTemp);
                        break;
                    }
                    catch (Exception)
                    {
                        WriteLine("线程数存在错误，请检查输入的参数！");
                        continue;
                    }
                } while (true);
                if (programParameter.DecryptArchiveThreadCount > (Environment.ProcessorCount - 1))
                {
                    WriteLine("进行测试线程数过高! 已调整为最大线程(" + (Environment.ProcessorCount - 1).ToString() + ")");
                    programParameter.DecryptArchiveThreadCount = Environment.ProcessorCount - 1;
                }
            }
            return true;
        }
        private static Dictionary<string, List<string>> RunProgram(FileInfo Program, string[] Arguments)
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

                    if (programParameter.DebugMode && !programParameter.FastDebugMode)
                    {
                        Console.Write(string.Join(Environment.NewLine, Output.ToArray()));
                        Console.Write(string.Join(Environment.NewLine, Error.ToArray()));
                    }
                    return new Dictionary<string, List<string>> { ["Output"] = Output, ["Error"] = Error };
                }
                catch (Exception ex)
                {
                    if (programParameter.DebugMode && !programParameter.FastDebugMode)
                    {
                        Console.Write(string.Join(Environment.NewLine, Output.ToArray()));
                        Console.Write(string.Join(Environment.NewLine, Error.ToArray()));
                        WriteLine(ex.ToString());
                    }
                    WriteLine("执行 "+ Program.FullName + string.Join(" ", Arguments)+ " 失败！");
                    throw ex;
                    
                }
                finally
                {
                    p.OutputDataReceived -= StoreOutput;
                    p.ErrorDataReceived -= StoreError;
                    p.Close();
                }
            }
        }

        public Main(string[] args, ProgramParameter Parameter)
        {
            programParameter = Parameter;
            RavenClient ravenClient = new RavenClient("https://9361b53d22da420c95bdb43d1b78eb1e@o687854.ingest.sentry.io/5773141");
            try
            {
                if (Initialize(args))
                {
                    MainProgram();
                }
                else
                {
                    WriteLine("初始化失败!");
                }
            }
            catch (Exception ex)
            {
                SentryEvent sentry = new SentryEvent(ex);
                string test = JsonConvert.SerializeObject(programParameter);
                sentry.Extra = test.ToString();
                ravenClient.Capture(sentry);
                WriteLine("错误信息已上报(按任意键退出程序)");
                Console.ReadKey();
                Process.Start(programParameter.AppHomePage);
            }
            return;
        }
        
        public class ParameterCompared : IEqualityComparer<Parameter>
        {
            public bool Equals(Parameter x, Parameter y)
            {
                if (x.PassWord == null && y.PassWord == null)
                    return true;
                if (x.PassWord == y.PassWord)
                    return true;
                return false;
            }

            public int GetHashCode(Parameter obj)
            {
                return obj.PassWord.GetHashCode();
            }
        }


        private void MainProgram()
        {

            if (programParameter.ArchiveFile.Extension.ToLower().Contains("rar"))
            {
                WriteLine("RAR格式压缩包！由于RAR专利问题需要调用完整版7Zip！");
                WriteLine("本程序将会读取注册表中的7Zip安装路径，如果不想程序读取，请直接关闭本程序。");
                WriteLine("请确保已安装完整版7zip！(按任意键继续！)");
                Console.ReadKey();
                WriteLine("正在查找7Zip路径...（正在搜索注册表）");
                if (string.IsNullOrEmpty(ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString()))
                {
                    WriteLine("调用完全体7Zip失败,请检查7Zip安装情况!");
                    Process.Start("https://sparanoid.com/lab/7z/");
                    throw new Exception("7Zip Error!");
                }
                else
                {
                    programParameter.ArchiveDecryptionProgram = new FileInfo(ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString() + "7z.exe");
                }
            }

            if (!RunProgram(programParameter.ArchiveDecryptionProgram, new string[] { "t", "\"" + programParameter.ArchiveFile.FullName + "\"", "-p" }).TryGetValue("Output", out List<string> Output))
            {
                WriteLine("压缩包损坏 或 不是支持的压缩包！");
                throw new Exception("ArchiveFile Error!");
            }
            else
            {
                if (Output.Where(p => p != null && p.Contains("Everything is Ok")).Any())
                {
                    WriteLine("非加密压缩包！（按任意键退出）");
                    Console.ReadKey();
                    return;
                }
            }


            List<Parameter> Dictionary = new List<Parameter>();
            
            try
            {
                Random r = new Random();
                WriteLine("正在读取字典...");
                using (FileStream LocalDictionary = new FileStream(programParameter.AppPath + "PasswordDictionary.txt", FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    using (StreamReader sr = new StreamReader(LocalDictionary, new UTF8Encoding(false)))
                    {
                        while (sr.Peek() != -1)
                        {
                            string item = sr.ReadLine();
                            if (item != null)
                            {
                                Dictionary.Add(new Parameter(r.Next(0, programParameter.DecryptArchiveThreadCount), item));
                            }
                        }

                        int DictionaryCount = Dictionary.Count;
                        WriteLine("从默认字典中读取到[" + DictionaryCount + "]个密码");

                        if (programParameter.Dictionary.FullName != programParameter.AppPath + "PasswordDictionary.txt")
                        {
                            using (FileStream DictionaryFile = new FileStream(programParameter.Dictionary.FullName, FileMode.Open, FileAccess.Read))
                            {
                                using (StreamReader Reader = new StreamReader(DictionaryFile, new UTF8Encoding(false)))
                                {
                                    while (Reader.Peek() != -1)
                                    {
                                        string item = Reader.ReadLine();
                                        if (item != null)
                                        {
                                            Dictionary.Add(new Parameter(r.Next(0, programParameter.DecryptArchiveThreadCount), item));
                                        }
                                    }
                                }
                            }
                            WriteLine("从新提供的字典中读取到[" + (Dictionary.Count - DictionaryCount) + "]个密码");
                        }

                        DictionaryCount = Dictionary.Count;
                        Write("正在对字典去重处理,请稍后...");
                        Dictionary = Dictionary.Distinct(new ParameterCompared()).ToList();
                        Console.WriteLine("完成![" + DictionaryCount + "] => [" + Dictionary.Count + "]");
                        Write("正在保存字典,请稍后(请勿关闭软件,以免造成数据丢失!)...");
                        LocalDictionary.SetLength(0);
                        using (StreamWriter SaveLocalDictionary = new StreamWriter(LocalDictionary, new UTF8Encoding(false)))
                        {
                            foreach (var item in Dictionary)
                            {
                                SaveLocalDictionary.WriteLine(item.PassWord);
                            }
                        }
                    }
                    Console.WriteLine("完成!");
                }
            }
            catch (Exception ex)
            {
                WriteLine("尝试读取密码字典时出现错误!");
                if (programParameter.DebugMode) { WriteLine(ex.ToString()); }
                throw ex;
            }

            Stopwatch sw = new Stopwatch();
            sw.Restart();

            using (ConsoleExpand ConsoleCanvas = new ConsoleExpand())
            {
                for (int i = 0; i < programParameter.DecryptArchiveThreadCount; i++)
                {
                    ConsoleCanvas.Print(0, i, "[" + i + "] 启动中...");
                }
                Parallel.ForEach(Dictionary, new ParallelOptions() { MaxDegreeOfParallelism = programParameter.DecryptArchiveThreadCount }, (i, loopState) => {
                    ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 测试中...");

                    if (RunProgram(programParameter.ArchiveDecryptionProgram, new string[] { "t", "\"" + programParameter.ArchiveFile.FullName + "\"", "-p\"" + i.PassWord + "\"" }).TryGetValue("Output", out List<string> OutputInfo))
                    {
                        if (OutputInfo.Where(p => p != null && p.Contains("Everything is Ok")).Any())
                        {
                            programParameter.EncryptArchivePassword = i.PassWord;
                            loopState.Stop();
                            ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 正确");
                        }
                        else
                        {
                            ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 错误");
                        }
                    }
                    else
                    {
                        ConsoleCanvas.Print(0, i.Thread, "[" + i.Thread + "] 密码: [" + i.PassWord + "] 错误");
                    }
                });
            }

            sw.Stop();

            Console.Clear();

            if (programParameter.EncryptArchivePassword != null)
            {
                WriteLine("已找到解压密码: " + programParameter.EncryptArchivePassword);
                WriteLine("耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
            }
            else
            {
                WriteLine("已测试 [" + Dictionary.Count + "] 个密码, 没有找到正确的解压密码。");
                WriteLine("耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
            }


            WriteLine("是否保存测试结果?(按回车键保存并退出/按其他任意键不保存并退出)");
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.Enter:
                    using (StreamWriter file = new StreamWriter(programParameter.AppPath + Path.GetFileName(programParameter.ArchiveFile.Name) + "[测试报告].txt", false))
                    {
                        file.WriteLine("加密压缩包: " + programParameter.ArchiveFile);
                        file.WriteLine("字典: " + programParameter.Dictionary);
                        if (programParameter.EncryptArchivePassword != null)
                        {
                            file.WriteLine("解压密码: " + programParameter.EncryptArchivePassword);
                        }
                        else
                        {
                            file.WriteLine("没有找到正确的解压密码！");
                        }
                        file.Write("耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                        file.Close();
                    }
                    Process.Start("Explorer.exe", "/select, \"" + programParameter.AppPath + Path.GetFileName(programParameter.ArchiveFile.Name) + "[测试报告].txt" + "\"");
                    break;
                default:
                    break;
            }
            return;
        }

    }
}
