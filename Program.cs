using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

using module.dawnlc.me;


namespace ArchivePasswordTestTool
{
    public class ProgramParameter
    {
        public static readonly string AppName = Assembly.GetExecutingAssembly().FullName.Substring(0, Assembly.GetExecutingAssembly().FullName.IndexOf(","));
        public static readonly string AppPath = Environment.CurrentDirectory + "\\";
        public static readonly int[] Version = new int[] { 1, 0, 8 };
        public static readonly string VersionType = "Release";
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
                    IO.ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources.7z.exe", AppPath + "7z.exe");
                }
                else
                {
                    using (FileStream data = new FileStream(AppPath + "7z.exe", FileMode.Open, FileAccess.Read))
                    {
                        if (!IO.ComparisonFile(Assembly.GetExecutingAssembly().GetManifestResourceStream(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources.7z.exe"), data))
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
        /// 重定向程序集解析
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
            }
            catch (IOException)
            {
                MemoryStream memoryStream = new MemoryStream();
                System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources." + assemblyName[0] + ".dll").CopyTo(memoryStream);
                return Assembly.Load(memoryStream.ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine("加载程序集出错！请前往：" + ProgramParameter.AppHomePage + " 提交错误信息。");
                Console.WriteLine(ex.ToString());
            }
            return null;
        }

        
        static void Main(string[] args)
        {
            new ProgramParameter();

            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            Main main = new Main(args);
            
        }

    }
}
