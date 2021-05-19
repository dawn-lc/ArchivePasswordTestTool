using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace module.dawnlc.me
{
    /// <summary>
    /// Console拓展 模块
    /// </summary>
    class ConsoleExpand : IDisposable
    {
        private bool isDisposed = false;
        private bool RenderCacheLock = false;
        /// <summary>
        /// Console 高
        /// </summary>
        public static int High = Console.WindowHeight;
        /// <summary>
        /// Console 宽
        /// </summary>
        public static int Width = Console.WindowWidth;

        public static Thread RenderThread;

        public static List<Content> RenderCache1 = new List<Content>();
        public static List<Content> RenderCache2 = new List<Content>();

        public class Content
        {
            public int Row { get; set; }
            public int Col { get; set; }
            public string ContentString { get; set; }
            public Content(int row, int col, string content)
            {
                Row = row;
                Col = col;
                ContentString = content;
            }
        }

        public ConsoleExpand()
        {
            Console.SetCursorPosition(0,0);
            Console.Clear();
            RenderThread = new Thread(OutputCanvas)
            {
                IsBackground = true
            };
            RenderThread.Start();
        }

        public void OutputCanvas()
        {
            while (true)
            {
                RenderCacheLock = true;
                IEnumerable<Content> Rendering = RenderCache1.Where(p => p != null);
                foreach (var item in Rendering)
                {
                    Console.SetCursorPosition(0, item.Row);
                    Console.Write(new string(' ', Width));
                    Console.SetCursorPosition(item.Col, item.Row);
                    Console.Write(item.ContentString);
                }
                RenderCache1 = RenderCache1.Except(Rendering).ToList();
                RenderCache1 = RenderCache1.Union(RenderCache2).Where(p => p != null).ToList();
                RenderCacheLock = false;
                RenderCache2.Clear();
                Thread.Sleep(32);
            }
        }

        public void Print(int Col, int Row, string Content)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Content))
                {
                    if (RenderCacheLock)
                    {
                        RenderCache2.Add(new Content(Row, Col, Content));
                    }
                    else
                    {
                        RenderCache1.Add(new Content(Row, Col, Content));
                    }
                }
                return;
            }
            catch (Exception)
            {
                return;
            }
        }
        public void Close()
        {
            Dispose(true);
        }
        ~ConsoleExpand()
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
            RenderThread.Abort();
            RenderCacheLock = true;
            RenderCache1 = RenderCache1.Union(RenderCache2).ToList();
            RenderCacheLock = false;
            foreach (var item in RenderCache1)
            {
                Console.SetCursorPosition(0, item.Row);
                Console.Write(new string(' ', Width));
                Console.SetCursorPosition(item.Col, item.Row);
                Console.Write(item.ContentString);
            }
            if (!isDisposed)
            {
                if (disposing)
                {
                    RenderCache2.Clear();
                    RenderCache1.Clear();
                }
            }
            isDisposed = true; // 标识此对象已释放
        }
    }
}
