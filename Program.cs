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

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 获取文件的编码格式
    /// </summary>
    public class EncodingType
    {
        /// <summary>
        /// 给定文件的路径，读取文件的二进制数据，判断文件的编码类型
        /// </summary>
        /// <param name=“FILE_NAME“>文件路径</param>
        /// <returns>文件的编码类型</returns>
        public static System.Text.Encoding GetType(string FILE_NAME)
        {
            FileStream fs = new FileStream(FILE_NAME, FileMode.Open, FileAccess.Read);
            Encoding r = GetType(fs);
            fs.Close();
            return r;
        }

        /// <summary>
        /// 通过给定的文件流，判断文件的编码类型
        /// </summary>
        /// <param name=“fs“>文件流</param>
        /// <returns>文件的编码类型</returns>
        public static System.Text.Encoding GetType(Stream fs)
        {
            byte[] Unicode = new byte[] { 0xFF, 0xFE, 0x41 };
            byte[] UnicodeBIG = new byte[] { 0xFE, 0xFF, 0x00 };
            byte[] UTF8 = new byte[] { 0xEF, 0xBB, 0xBF }; //带BOM
            Encoding reVal = Encoding.Default;

            BinaryReader r = new BinaryReader(fs, System.Text.Encoding.Default);
            int i;
            int.TryParse(fs.Length.ToString(), out i);
            byte[] ss = r.ReadBytes(i);
            if (IsUTF8Bytes(ss) || (ss[0] == 0xEF && ss[1] == 0xBB && ss[2] == 0xBF))
            {
                reVal = Encoding.UTF8;
            }
            else if (ss[0] == 0xFE && ss[1] == 0xFF && ss[2] == 0x00)
            {
                reVal = Encoding.BigEndianUnicode;
            }
            else if (ss[0] == 0xFF && ss[1] == 0xFE && ss[2] == 0x41)
            {
                reVal = Encoding.Unicode;
            }
            r.Close();
            return reVal;

        }

        /// <summary>
        /// 判断是否是不带 BOM 的 UTF8 格式
        /// </summary>
        /// <param name=“data“></param>
        /// <returns></returns>
        private static bool IsUTF8Bytes(byte[] data)
        {
            int charByteCounter = 1; //计算当前正分析的字符应还有的字节数
            byte curByte; //当前分析的字节.
            for (int i = 0; i < data.Length; i++)
            {
                curByte = data[i];
                if (charByteCounter == 1)
                {
                    if (curByte >= 0x80)
                    {
                        //判断当前
                        while (((curByte <<= 1) & 0x80) != 0)
                        {
                            charByteCounter++;
                        }
                        //标记位首位若为非0 则至少以2个1开始 如:110XXXXX...........1111110X
                        if (charByteCounter == 1 || charByteCounter > 6)
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    //若是UTF-8 此时第一位必须为1
                    if ((curByte & 0xC0) != 0x80)
                    {
                        return false;
                    }
                    charByteCounter--;
                }
            }
            if (charByteCounter > 1)
            {
                throw new Exception("非预期的byte格式");
            }
            return true;
        }

    }

    public class ProgramParameter
    {
        public ProgramParameter()
        {
            ProgramParameter.AppPath = Environment.CurrentDirectory + "\\";
            ProgramParameter.ArchiveDecryptionProgram = null;
            ProgramParameter.ArchiveFile = null;
            ProgramParameter.ArchiveFileType = null;
            ProgramParameter.FileSize = 0;
            ProgramParameter.FileName = null;
            ProgramParameter.Dictionary = null;
            ProgramParameter.EncryptArchivePassword = null;
            ProgramParameter.DecryptArchiveThreadCount = 1;
            ProgramParameter.EncryptArchiveFileDecryptComplete = false;
            ProgramParameter.DebugMode = false;
            ProgramParameter.FastDebugMode = false;
        }
        public static readonly string Version = "1.0.2";
        public static string AppPath { get; set; }
        public static string ArchiveDecryptionProgram { get; set; }
        public static string ArchiveFile { get; set; }
        public static string ArchiveFileType { get; set; }
        public static long FileSize { get; set; }
        public static string FileName { get; set; }
        public static string Dictionary { get; set; }
        public static string EncryptArchivePassword { get; set; }
        public static int DecryptArchiveThreadCount { get; set; }
        public static bool EncryptArchiveFileDecryptComplete { get; set; }
        public static bool DebugMode { get; set; }
        public static bool FastDebugMode { get; set; }
    }
    class Program
    {
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

        static void Main(string[] args)
        {
            new ProgramParameter();
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            
            if (Initialize(args))
            {
                string[] Data;
                Data = DecryptArchiveFile("t \"" + ProgramParameter.ArchiveFile + "\" -p", ProgramParameter.ArchiveDecryptionProgram);
                if (Data[0].Contains("Testing archive:"))
                {
                    if (Data[0].Contains("Everything is Ok"))
                    {
                        Console.WriteLine("非加密压缩包！（按任意键退出）");
                        Console.ReadKey();
                        return;
                    }
                    else
                    {
                        if (Data[1].Contains("Can not open encrypted archive. Wrong password?"))
                        {
                            ProgramParameter.FileName = null;
                        }
                        else
                        {
                            Data = DecryptArchiveFile("l \"" + ProgramParameter.ArchiveFile + "\"", ProgramParameter.ArchiveDecryptionProgram);
                            foreach (var item in GetContent(Data[0], "  ------------------------", "------------------- -----").Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string[] FileInfo = item.Split(new string[] { " " }, 6, StringSplitOptions.RemoveEmptyEntries);
                                if (ProgramParameter.FileSize == 0 && Convert.ToInt64(FileInfo[4]) > 0)
                                {
                                    ProgramParameter.FileSize = Convert.ToInt64(FileInfo[4]);
                                    ProgramParameter.FileName = FileInfo[5];
                                }
                                if (Convert.ToInt64(FileInfo[4]) != ProgramParameter.FileSize && Convert.ToInt64(FileInfo[4]) > 0)
                                {
                                    if (ProgramParameter.FileSize > Convert.ToInt64(FileInfo[4]))
                                    {
                                        ProgramParameter.FileSize = Convert.ToInt64(FileInfo[4]);
                                        ProgramParameter.FileName = FileInfo[5];
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("压缩包损坏 或 不是支持的压缩包！（按任意键退出）");
                    Console.ReadKey();
                    return;
                }
                string[] DictionaryData;
                try
                {
                    using (StreamReader sr = new StreamReader(ProgramParameter.Dictionary, EncodingType.GetType(ProgramParameter.Dictionary)))
                    {
                        DictionaryData = sr.ReadToEnd().Split(new string[] { "\r\n" }, StringSplitOptions.None).Distinct().ToArray();
                        sr.Close();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    return;
                }


                Stopwatch sw = new Stopwatch();
                sw.Restart();
                ManualResetEvent[] ManualEvents = new ManualResetEvent[ProgramParameter.DecryptArchiveThreadCount];
                int piece = 0;
                for (int i = 0; i < ProgramParameter.DecryptArchiveThreadCount; i++)
                {
                    string[] DictionaryPiece = new string[] { };
                    if ((i + 1) != ProgramParameter.DecryptArchiveThreadCount)
                    {
                        DictionaryPiece = DictionaryData.Skip(piece).Take(DictionaryData.Length / ProgramParameter.DecryptArchiveThreadCount).ToArray();
                        piece += DictionaryData.Length / ProgramParameter.DecryptArchiveThreadCount;
                    }
                    else
                    {
                        DictionaryPiece = DictionaryData.Skip(piece).Take(DictionaryData.Length).ToArray();
                    }

                    ManualEvents[i] = new ManualResetEvent(false);
                    ThreadPool.QueueUserWorkItem(new WaitCallback(DecryptArchiveFileThread), new Parameter("线程" + i.ToString(), DictionaryPiece, ManualEvents[i]));
                }
                WaitHandle.WaitAll(ManualEvents);
                sw.Stop();
                if (ProgramParameter.EncryptArchivePassword != null)
                {
                    string DictionaryString = ProgramParameter.EncryptArchivePassword;
                    foreach (var item in DictionaryData.Distinct())
                    {
                        if (item != ProgramParameter.EncryptArchivePassword)
                        {
                            DictionaryString += "\r\n" + item;
                        }
                    }
                    using (StreamWriter file = new StreamWriter(ProgramParameter.Dictionary, false))
                    {
                        file.Write(DictionaryString);
                        file.Close();
                    }
                    Console.WriteLine("\r\n已找到解压密码: \r\n" + ProgramParameter.EncryptArchivePassword + "\r\n共耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                }
                else
                {
                    Console.WriteLine("已测试 [" + DictionaryData.Length + "] 个密码, 没有找到正确的解压密码. 耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff"));
                }
                Console.Write("是否保存测试结果?(按回车键保存并退出/按其他任意键不保存并退出)");
                switch (Console.ReadKey().Key)
                {
                    case ConsoleKey.Enter:
                        using (StreamWriter file = new StreamWriter(ProgramParameter.AppPath + Path.GetFileName(ProgramParameter.ArchiveFile) + "[测试报告].txt", false))
                        {
                            file.WriteLine("加密压缩包: " +ProgramParameter.ArchiveFile);
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
                        Process.Start("Explorer.exe", "/select, \"" + ProgramParameter.AppPath + Path.GetFileName(ProgramParameter.ArchiveFile) + "[测试报告].txt" + "\"");
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

        public class Parameter
        {
            public Parameter(string name, string[] DictionaryData, WaitHandle doneEvent)
            {
                this.DictionaryData = DictionaryData;
                this.DoneEvent = doneEvent;
                this.Name = name;
            }

            public string[] DictionaryData { get; }
            public WaitHandle DoneEvent { get; set; }
            public string Name { get; }
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
        /// Checks the file is textfile or not.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>     
        public static bool CheckIsTextFile(string fileName)
        {
            FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            bool isTextFile = true;
            try
            {
                int i = 0;
                int length = (int)fs.Length;
                byte data;
                while (i < length && isTextFile)
                {
                    data = (byte)fs.ReadByte();
                    isTextFile = (data != 0); i++;
                }
                return isTextFile;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (fs != null)
                {
                    fs.Close();
                }
            }
        }
        public static string HttpGet(string url, int timeOut)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            HttpWebRequest Web_Request = (HttpWebRequest)WebRequest.Create(url);
            Web_Request.AllowAutoRedirect = false;
            Web_Request.Timeout = timeOut;
            Web_Request.Method = "GET";
            Web_Request.UserAgent = "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/87.0.4280.88 Safari/537.36";
            Web_Request.ContentType = "charset=UTF-8;";
            try
            {
                HttpWebResponse Web_Response = (HttpWebResponse)Web_Request.GetResponse();
                if (Web_Response.ContentEncoding.ToLower().Contains("gzip"))
                {
                    using (Stream Stream_Receive = Web_Response.GetResponseStream())
                    {
                        using (new GZipStream(Stream_Receive, CompressionMode.Decompress))
                        {
                            using (StreamReader Stream_Reader = new StreamReader(Stream_Receive, Encoding.UTF8))
                            {
                                return Stream_Reader.ReadToEnd();
                            }
                        }
                    }
                }
                else
                {
                    using (Stream stream = Web_Response.GetResponseStream())
                    {
                        using (StreamReader streamReader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return streamReader.ReadToEnd();
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        /// <summary>
        /// 取中间文本
        /// </summary>
        /// <param name="Str">全文</param>
        /// <param name="First">前文</param>
        /// <param name="Final">后文</param>
        /// <returns>如果Str中不包含First或Final,返回null. 如果Str中包含First和Final,返回First和Final中的文本.</returns>
        public static string GetContent(string Str, string First, string Final)
        {
            if (Str.Contains(First) && Str.Contains(Final))
            {
                int FirstPosition = Str.IndexOf(First) + First.Length;
                int FinalPosition = Str.IndexOf(Final, FirstPosition) - FirstPosition;
                return Str.Substring(FirstPosition, FinalPosition);
            }
            else
            {
                return null;
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
        
        private static bool StartupParametersCheck(List<string> StartupParameters,string ParameterFlag)
        {
            if (StartupParameters.Contains(ParameterFlag))
            {
                try
                {
                    if(!string.IsNullOrEmpty(StartupParameters[StartupParameters.IndexOf(ParameterFlag) + 1]))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.Write("启动参数存在错误！请检查参数：["+ ParameterFlag +"]");
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

            try
            {
                Console.WriteLine(HttpGet("https://api.github.com/repos/dawn-lc/ArchivePasswordTestTool/releases/latest", 3000));
                Console.ReadLine();
                JObject ReleasesLatest = (JObject)JsonConvert.DeserializeObject(HttpGet("https://api.github.com/repos/dawn-lc/ArchivePasswordTestTool/releases/latest", 3000));
                if (ReleasesLatest["tag_name"].ToString() != ProgramParameter.Version)
                {
                    while (true)
                    {
                        Console.WriteLine("有可用的更新！是否前往查看?");
                        Console.Write("(按Y前往查看更新/按N退出): ");
                        switch (Console.ReadKey().Key)
                        {
                            case ConsoleKey.Y:
                                Console.Clear();
                                Process.Start("https://www.bilibili.com/read/cv6101558");
                                break;
                            case ConsoleKey.N:
                                return false;
                            default:
                                Console.WriteLine();
                                Console.WriteLine("输入错误!");
                                continue;
                        }
                        break;
                    }
                }
                else
                {
                    Console.WriteLine("当前已是最新版本。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("检查更新失败！请检查您的网络情况。");
                Process.Start("https://www.bilibili.com/read/cv6101558");
                if (ProgramParameter.DebugMode) { Console.WriteLine(ex.ToString()); }
            }
            
            
            if (!File.Exists(ProgramParameter.AppPath + "7z.exe"))
            {
                ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe", ProgramParameter.AppPath + "7z.exe");
            }
            else
            {
                StringBuilder MD5String = new StringBuilder();
                foreach (var MD5byte in MD5.Create().ComputeHash(Assembly.GetExecutingAssembly().GetManifestResourceStream(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe")))
                {
                    MD5String.Append(MD5byte.ToString("X2"));
                }
                string Builtin7ZipHash = MD5String.ToString();
                MD5String.Clear();
                using (FileStream data = new FileStream(ProgramParameter.AppPath + "7z.exe", FileMode.Open, FileAccess.Read))
                {
                    foreach (var MD5byte in MD5.Create().ComputeHash(data))
                    {
                        MD5String.Append(MD5byte.ToString("X2"));
                    }
                    data.Close();
                }
                string External7ZipHash = MD5String.ToString();
                MD5String.Clear();
                if (Builtin7ZipHash != External7ZipHash)
                {
                    while (true)
                    {
                        Console.WriteLine("注意！校验到7Zip.exe与本程序自带的不一致，可能会导致程序无法正常工作。");
                        Console.Write("是否使用本程序自带的7Zip.exe版本将其覆盖？(按Y继续/按N退出): ");
                        switch (Console.ReadKey().Key)
                        {
                            case ConsoleKey.Y:
                                Console.Clear();
                                ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe", ProgramParameter.AppPath + "7z.exe");
                                break;
                            case ConsoleKey.N:
                                return false;
                            default:
                                Console.WriteLine();
                                Console.WriteLine("输入错误!");
                                continue;
                        }
                        break;
                    }
                }
            }

            if (StartupParametersCheck(StartupParameters, "-F"))
            {
                try
                {
                    ProgramParameter.ArchiveFile = Path.GetFullPath(StartupParameters[StartupParameters.IndexOf("-F") + 1]);
                }
                catch (Exception ex)
                {
                    Console.Write("启动参数存在错误！请检查参数：[-F]");
                    Console.ReadLine();
                    if (ProgramParameter.DebugMode) { Console.Write(ex.ToString()); }
                    return false;
                }

                if (!File.Exists(ProgramParameter.ArchiveFile))
                {
                    do
                    {
                        Console.WriteLine("没有找到您的压缩包[" + ProgramParameter.ArchiveFile + "]!");
                        Console.WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        ProgramParameter.ArchiveFile = Console.ReadLine();
                        ProgramParameter.ArchiveFile = DelQuotationMarks(ProgramParameter.ArchiveFile);
                    } while (!File.Exists(ProgramParameter.ArchiveFile));
                }
            }
            else
            {
                do
                {
                    Console.WriteLine("您似乎没有提供需要进行测试的压缩包地址!");
                    Console.WriteLine("请将压缩包拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                    ProgramParameter.ArchiveFile = Console.ReadLine();
                    ProgramParameter.ArchiveFile = DelQuotationMarks(ProgramParameter.ArchiveFile);
                } while (!File.Exists(ProgramParameter.ArchiveFile));
            }

            if (Path.GetExtension(ProgramParameter.ArchiveFile) == string.Empty || Path.GetExtension(ProgramParameter.ArchiveFile) == null)
            {
                Console.WriteLine("错误的压缩包路径 或 未知的压缩包拓展名!(按任意键退出程序)");
                Console.ReadKey();
                return false;
            }
            else
            {
                ProgramParameter.ArchiveFileType = Path.GetExtension(ProgramParameter.ArchiveFile);
                string[] data = DecryptArchiveFile("t \"" + ProgramParameter.ArchiveFile + "\" -p", ProgramParameter.ArchiveDecryptionProgram);
                if (string.IsNullOrEmpty(data[0]))
                {
                    while (true)
                    {
                        Console.Write("无法识别压缩包格式! 是否继续尝试测试密码?(按Y继续/按N退出): ");
                        switch (Console.ReadKey().Key)
                        {
                            case ConsoleKey.Y:
                                Console.Clear();
                                break;
                            case ConsoleKey.N:
                                return false;
                            default:
                                Console.WriteLine();
                                Console.WriteLine("输入错误!");
                                continue;
                        }
                        break;
                    }
                }
                else
                {
                    if (data[0].IndexOf("Type = ") == -1)
                    {
                        while (true)
                        {
                            if (data[1].IndexOf("Can not open the file as archive") != -1)
                            {
                                using (BinaryReader FileData = new BinaryReader(new FileStream(ProgramParameter.ArchiveFile, FileMode.Open, FileAccess.Read)))
                                {
                                    string FileHeaderData=null;
                                    for (int i = 0; i < 4; i++)
                                    {
                                        FileHeaderData += Convert.ToString(FileData.ReadByte(),16);
                                    }
                                    FileData.Close();
                                    switch (FileHeaderData)
                                    {
                                        case "52617221":
                                            Console.Clear();
                                            Console.WriteLine("压缩包为RAR格式, 需要调用完全体7Zip! ");
                                            Console.WriteLine("若您已经安装7Zip, 请按回车键确认继续 或 其他任意键退出程序.");
                                            if (Console.ReadKey().Key != ConsoleKey.Enter)
                                            {
                                                return false;
                                            }
                                            Console.Clear();
                                            if (string.IsNullOrEmpty(ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString()))
                                            {
                                                Console.WriteLine("调用完全体7Zip失败,请检查7Zip安装情况!(按任意键退出程序)");
                                                Console.ReadKey();
                                                Process.Start("https://sparanoid.com/lab/7z/");
                                                return false;
                                            }
                                            else
                                            {
                                                ProgramParameter.ArchiveDecryptionProgram = ReadRegeditValue("SOFTWARE\\7-Zip", "Path").ToString() + "7z.exe";
                                            }
                                            break;
                                        default:
                                            Console.Write("这似乎不是一个压缩包，请问是否继续尝试测试密码?(按Y继续/按N退出): ");
                                            switch (Console.ReadKey().Key)
                                            {
                                                case ConsoleKey.Y:
                                                    Console.Clear();
                                                    break;
                                                case ConsoleKey.N:
                                                    return false;
                                                default:
                                                    Console.WriteLine();
                                                    Console.WriteLine("输入错误!");
                                                    continue;
                                            }
                                            break;
                                    }
                                }
                                break;
                            }
                            else
                            {
                                Console.Write("压缩包被完全加密! 是否继续尝试测试密码?(按Y继续/按N退出): ");
                                switch (Console.ReadKey().Key)
                                {
                                    case ConsoleKey.Y:
                                        Console.Clear();
                                        break;
                                    case ConsoleKey.N:
                                        return false;
                                    default:
                                        Console.WriteLine();
                                        Console.WriteLine("输入错误!");
                                        continue;
                                }
                                break;
                            }
                        }
                    }
                    else
                    {
                        ProgramParameter.ArchiveFileType = "." + GetContent(data[0], "Type = ", "Physical Size = ").ToLower();
                    }
                }
            }

            if (StartupParametersCheck(StartupParameters, "-D"))
            {
                if (File.Exists(StartupParameters[StartupParameters.IndexOf("-D") + 1]))
                {
                    ProgramParameter.Dictionary = StartupParameters[StartupParameters.IndexOf("-D") + 1];
                }
                else
                {
                    do
                    {
                        Console.WriteLine("没有找到您的密码字典[" + ProgramParameter.Dictionary + "]!");
                        Console.WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        ProgramParameter.Dictionary = Console.ReadLine();
                        ProgramParameter.Dictionary = DelQuotationMarks(ProgramParameter.Dictionary);
                    } while (!File.Exists(ProgramParameter.Dictionary));
                }
            }
            else
            {
                if (File.Exists(ProgramParameter.AppPath + "PasswordDictionary.txt"))
                {
                    ProgramParameter.Dictionary = ProgramParameter.AppPath + "PasswordDictionary.txt";
                }
                else
                {
                    do
                    {
                        Console.WriteLine("您似乎没有提供您的密码字典地址!");
                        Console.WriteLine("请将密码字典拖放到本窗口，或手动输入文件地址。(操作完成后, 按回车键继续)");
                        ProgramParameter.Dictionary = Console.ReadLine();
                        ProgramParameter.Dictionary = DelQuotationMarks(ProgramParameter.Dictionary);
                    } while (!File.Exists(ProgramParameter.Dictionary));
                }
            }

            if (Path.GetExtension(ProgramParameter.Dictionary).ToLower() != ".txt")
            {
                Console.WriteLine("错误的字典文件格式!(按任意键退出程序)");
                Console.ReadKey();
                return false;
            }
            else
            {
                if (!CheckIsTextFile(ProgramParameter.Dictionary))
                {
                    Console.WriteLine("错误的字典文件格式!(按任意键退出程序)");
                    Console.ReadKey();
                    return false;
                }
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
        static string[] DecryptArchiveFile(string DecryptArguments, string ArchiveDecryptionProgram = null)
        {
            string Error = null;
            string Output = null;
            
            if (ArchiveDecryptionProgram == null)
            {
                ArchiveDecryptionProgram = ProgramParameter.AppPath + "7z.exe";
            }
            using (Process p = new Process())
            {
                void StoreError(object o, DataReceivedEventArgs e) {
                    Error += e.Data + "\r\n";
                }
                void StoreOutput(object o, DataReceivedEventArgs e) {
                    Output += e.Data + "\r\n";
                }
                try
                {
                    p.StartInfo.FileName = ArchiveDecryptionProgram;
                    p.StartInfo.Arguments = DecryptArguments;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardInput = true;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.OutputDataReceived += StoreOutput;
                    p.ErrorDataReceived += StoreError;
                    p.Start();
                    p.StandardInput.AutoFlush = true;
                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();
                    p.StandardInput.WriteLine();
                    p.WaitForExit();
                    if (string.IsNullOrEmpty(Output)) Output = null;
                    if (string.IsNullOrEmpty(Error)) Error = null;
                    if (ProgramParameter.DebugMode && !ProgramParameter.FastDebugMode) {
                        Console.Write(Output);
                        Console.Write(Error);
                    }
                    return new string[] { Output, Error };
                }
                catch (Exception ex)
                {
                    Output = ex.ToString();
                    if (string.IsNullOrEmpty(Output)) Output = null;
                    return new string[] { Output, Error };
                }
                finally
                {
                    p.OutputDataReceived -= StoreOutput;
                    p.ErrorDataReceived -= StoreError;
                    p.Close();
                }

            }
        }

        private static string DelQuotationMarks(string data)
        {
            if (data.Skip(0).Take(1).ToArray()[0] == '"')
            {
                data = data.Remove(0, 1);
            }
            if (data.Skip(data.Length - 1).Take(1).ToArray()[0] == '"')
            {
                data = data.Remove(data.Length - 1, 1);
            }
            return data;
        }
        private static void DecryptArchiveFileThread(object data)
        {
            ManualResetEvent e = (ManualResetEvent)((Parameter)data).DoneEvent;
            string[] Dictionary = ((Parameter)data).DictionaryData;
            Stopwatch sw = new Stopwatch();
            for (int i = 0; i < Dictionary.Length; i++)
            {
                if (!ProgramParameter.EncryptArchiveFileDecryptComplete)
                {
                    if (ProgramParameter.EncryptArchiveFileDecryptComplete)
                    {
                        Console.WriteLine("[" + ((Parameter)data).Name + "] - 侦测到已有其他线程找到正确密码，本线程结束任务。");
                        e.Set();
                        return;
                    }
                    string[] Data;
                    Console.WriteLine("[" + ((Parameter)data).Name + "] - 正在测试: [" + Dictionary[i] + "]");
                    sw.Restart();
                    if (ProgramParameter.FileName == null)
                    {
                        Data = DecryptArchiveFile("t \"" + ProgramParameter.ArchiveFile + "\" -p\"" + Dictionary[i] + "\"", ProgramParameter.ArchiveDecryptionProgram);
                    }
                    else
                    {
                        Data = DecryptArchiveFile("t \"" + ProgramParameter.ArchiveFile + "\" -p\"" + Dictionary[i] + "\" \"" + ProgramParameter.FileName + "\"", ProgramParameter.ArchiveDecryptionProgram);
                    }
                    sw.Stop();
                    if (ProgramParameter.EncryptArchiveFileDecryptComplete)
                    {
                        Console.WriteLine("[" + ((Parameter)data).Name + "] - 侦测到已有其他线程找到正确密码，本线程结束任务。");
                        e.Set();
                        return;
                    }
                    if (Data[0] != null)
                    {
                        if (Data[0].Contains("Everything is Ok"))
                        {
                            ProgramParameter.EncryptArchiveFileDecryptComplete = true;
                            ProgramParameter.EncryptArchivePassword = Dictionary[i];
                            Console.WriteLine("[" + ((Parameter)data).Name + "] - 找到正确密码: [ " + Dictionary[i] + " ] [耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff") + " ]");
                            e.Set();
                            return;
                        }
                        else
                        {
                            Console.WriteLine("[" + ((Parameter)data).Name + "] - 密码: [" + Dictionary[i] + "] 不正确，测试完成。[耗时: " + sw.Elapsed.ToString(@"hh\:mm\:ss\.ffff") + " ]");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("[" + ((Parameter)data).Name + "] - 侦测到已有其他线程找到正确密码，本线程结束任务。");
                    e.Set();
                    return;
                }
            }
            Console.WriteLine("[" + ((Parameter)data).Name + "] - 已测试: [" + Dictionary.Length + "] 个密码,未找到正确密码.");
            e.Set();
            return;
        }
    }
}
