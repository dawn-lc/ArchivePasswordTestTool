using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using module.dawnlc.me;

namespace ArchivePasswordTestTool
{
    class Program
    {
        public static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string[] assemblyName = args.Name.Split(',');
            DirectoryInfo DirectoryInfo = new DirectoryInfo(Environment.CurrentDirectory + "\\Bin\\");
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
                Console.WriteLine("加载程序集出错！请向开发者提交以下错误信息。");
                Console.WriteLine(ex.ToString());
            }
            return null;
        }

        
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            new MainProgram(args);
        }

    }
}
