using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace module.dawnlc.me
{
    /// <summary>
    /// Console拓展 模块
    /// </summary>
    class ConsoleExpand : IDisposable
    {
        private bool isDisposed = false;
        /// <summary>
        /// Console 高
        /// </summary>
        public static int High = Console.WindowHeight;
        /// <summary>
        /// Console 宽
        /// </summary>
        public static int Width = Console.WindowWidth;

        public static Thread RenderThread;

        public static List<Content> RenderCache = new List<Content>();

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
                List<Content> Rendering = RenderCache.Where(p => p != null).ToList();
                foreach (var item in Rendering)
                {
                    Console.SetCursorPosition(0, item.Row);
                    Console.Write(new string(' ', Width));
                    Console.SetCursorPosition(item.Col, item.Row);
                    Console.Write(item.ContentString);
                }
                RenderCache = RenderCache.Except(Rendering).ToList();
                Thread.Sleep(32);
            }
        }

        public void Print(int Col, int Row, string Content)
        {
            RenderCache.Add(new Content(Row, Col, Content));
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
            List<Content> Rendering = RenderCache.Where(p => p != null).ToList();
            foreach (var item in Rendering)
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
                    RenderThread.Abort();
                }
            }
            isDisposed = true; // 标识此对象已释放
        }
    }
}
