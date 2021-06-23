using System;
using System.IO;
using System.Reflection;

using module.dawnlc.me;


namespace ArchivePasswordTestTool
{
    public class ProgramParameter
    {
        public readonly string AppName = Assembly.GetExecutingAssembly().FullName.Substring(0, Assembly.GetExecutingAssembly().FullName.IndexOf(","));
        public readonly string AppPath = Environment.CurrentDirectory + "\\";
        public readonly int[] Version = new int[] { 1, 0, 11 };
        public readonly string VersionType = "Release";
        public readonly string AppHomePage = "https://www.bilibili.com/read/cv6101558";
        public readonly string Developer = "dawn-lc";
        public bool DebugMode { get; set; }
        public bool FastDebugMode { get; set; }
        public FileInfo ArchiveDecryptionProgram { get; set; }
        public FileInfo ArchiveFile { get; set; }
        public FileInfo Dictionary { get; set; }
        public string EncryptArchivePassword { get; set; }
        public int DecryptArchiveThreadCount { get; set; }

        public ProgramParameter()
        {
            DebugMode = false;
            FastDebugMode = false;

            try
            {
                if (!Directory.Exists(AppPath + "Bin\\"))
                {
                    Directory.CreateDirectory(AppPath + "Bin\\");
                }
                if (!File.Exists(AppPath + "Bin\\7z.exe"))
                {
                    IO.ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources.7z.exe", AppPath + "Bin\\7z.exe");
                }
                else
                {
                    using (FileStream data = new FileStream(AppPath + "Bin\\7z.exe", FileMode.Open, FileAccess.Read))
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
                                        File.Delete(AppPath + "Bin\\7z.exe");
                                        IO.ExtractResFile(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".7z.exe", AppPath + "Bin\\7z.exe");
                                        break;
                                    case ConsoleKey.N:
                                        break;
                                    default:
                                        Console.Clear();
                                        continue;
                                }
                                break;
                            }
                        }
                    }
                }
                ArchiveDecryptionProgram = new FileInfo(AppPath + "Bin\\7z.exe");
            }
            catch (Exception)
            {
                Console.WriteLine("自检时出现错误！请确认读写权限是否开放或磁盘空间是否不足。(按任意键退出程序)");
                Console.ReadKey();
                Environment.Exit(0);
            }
            EncryptArchivePassword = null;
            DecryptArchiveThreadCount = 1;
        }
    }
    
    class Program
    {
        public static ProgramParameter programParameter = new ProgramParameter();
        public static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string[] assemblyName = args.Name.Split(',');
            DirectoryInfo DirectoryInfo = new DirectoryInfo(programParameter.AppPath + "Bin\\");
            try
            {
                if (!DirectoryInfo.Exists)
                {
                    Directory.CreateDirectory(DirectoryInfo.FullName);
                }
                if (!assemblyName[0].Contains(MethodBase.GetCurrentMethod().DeclaringType.Namespace))
                {
                    switch (assemblyName[0])
                    {
                        default:
                            return Assembly.LoadFrom(DirectoryInfo.FullName + assemblyName[0] + ".dll");
                    }
                }
            }
            catch (IOException)
            {
                using (MemoryStream assembly = new MemoryStream())
                {
                    Assembly.GetExecutingAssembly().GetManifestResourceStream(MethodBase.GetCurrentMethod().DeclaringType.Namespace + ".Resources." + assemblyName[0] + ".dll").CopyTo(assembly);
                    return Assembly.Load(assembly.ToArray());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("加载程序集出错！请前往：" + programParameter.AppHomePage + " 提交错误信息。");
                Console.WriteLine(ex.ToString());
            }
            return null;
        }

        
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);

            Main main = new Main(args, programParameter);
            
        }

    }
}
