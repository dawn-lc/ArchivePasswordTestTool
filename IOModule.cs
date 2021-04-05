using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace module.dawnlc.me
{
    class IO
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
        /// 释放资源文件
        /// </summary>
        /// <param name="resFileName">资源文件名</param>
        /// <param name="outputFile">输出文件路径</param>
        public static void ExtractResFile(string resFileName, string outputFile)
        {
            using (Stream inStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resFileName))
            {
                using (FileStream outStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                {
                    inStream.CopyTo(outStream);
                }
            }
        }
        /// <summary>
        /// 比较两个文件是否一致
        /// </summary>
        /// <param name="FileA">文件流</param>
        /// <param name="FileB">文件流</param>
        /// <returns>是否相等</returns>
        public static bool ComparisonFile(Stream FileA, Stream FileB)
        {
            try
            {
                using (MD5 FileMD5 = MD5.Create())
                {
                    if (Equals(FileMD5.ComputeHash(FileA), FileMD5.ComputeHash(FileB)))
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return false;
            }
        }
        public class FileTree : IDisposable
        {
            private bool isDisposed = false;
            public DirectoryInfo DirectoryInfo { get; set; }
            public FileInfo[] FileInfos { get; set; }
            public List<FileTree> ChildDirectory { get; set; }
            public FileTree(string path)
            {
                this.DirectoryInfo = new DirectoryInfo(path);
                this.FileInfos = this.DirectoryInfo.GetFiles();
                this.ChildDirectory = new List<FileTree>() { };
                foreach (var item in this.DirectoryInfo.GetDirectories())
                {
                    this.ChildDirectory.Add(new FileTree(item.FullName));
                }
            }
            public FileInfo[] GetFileInfosAll(FileTree fileTree = null)
            {
                if (fileTree == null) fileTree = this;
                FileInfo[] fileInfos = fileTree.FileInfos;
                foreach (var ChildDirectory in fileTree.ChildDirectory)
                {
                    fileInfos = fileInfos.Union(this.GetFileInfosAll(ChildDirectory)).ToArray();
                }
                return fileInfos;
            }
            public DirectoryInfo[] GetDirectoryInfosAll(FileTree fileTree = null)
            {
                if (fileTree == null) fileTree = this;
                DirectoryInfo[] DirectoryInfos = new DirectoryInfo[] { fileTree.DirectoryInfo };
                foreach (var ChildDirectory in fileTree.ChildDirectory)
                {
                    DirectoryInfos = DirectoryInfos.Union(this.GetDirectoryInfosAll(ChildDirectory)).ToArray();
                }
                return DirectoryInfos;
            }
            public void Close()
            {
                Dispose(true);
            }
            ~FileTree()
            {
                Dispose(false);
            }
            void IDisposable.Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
            protected virtual void Dispose(bool disposing)
            {
                if (!this.isDisposed)
                {
                    if (disposing)
                    {
                        this.DirectoryInfo = null;
                        this.FileInfos = null;
                        foreach (var ChildDirectory in this.ChildDirectory)
                        {
                            ChildDirectory.Close();
                        }
                        this.ChildDirectory = null;
                    }
                }
                this.isDisposed = true;
            }
        }
        public static byte[] StreamRead(Stream stream, long a, long b)
        {
            if ((b - a) > int.MaxValue)
            {
                throw new OverflowException();
            }
            int DataLength = (int)(b - a);
            byte[] ReadData = new byte[DataLength];
            stream.Seek(a, SeekOrigin.Begin);
            stream.Read(ReadData, 0, DataLength);
            return ReadData;
        }
    }
}
