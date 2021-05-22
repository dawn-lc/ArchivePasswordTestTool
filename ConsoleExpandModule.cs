using System;
using System.Collections.Generic;
using System.Threading;

namespace module.dawnlc.me
{
    /// <summary>
    /// Console拓展 模块
    /// </summary>
    class ConsoleExpand : IDisposable
    {
        private bool isDisposed = false;
        private bool isDisposing = false;
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
            while (!isDisposing)
            {
                RenderCacheLock = true;
                List<Content> Rendering = new List<Content>(RenderCache1);
                foreach (var item in Rendering)
                {
                    if (item != null)
                    {
                        Console.SetCursorPosition(0, item.Row);
                        Console.Write(new string(' ', Width));
                        Console.SetCursorPosition(item.Col, item.Row);
                        Console.Write(item.ContentString);
                    }

                }
                Rendering.Clear();
                RenderCache1.Clear();

                RenderCache1.AddRange(RenderCache2);
                RenderCacheLock = false;

                RenderCache2.Clear();

                Thread.Sleep(32);
            }
        }

        public void Print(int Col, int Row, string Content)
        {
            try
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
            catch (Exception)
            {
                //这里如果报错,肯定是破解速度过快.(跑完了7zip test并返回了结果只花了不到10毫秒)
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
            if (!isDisposed)
            {
                if (disposing)
                {
                    RenderThread.Abort();
                    isDisposing = true;

                    foreach (var item in RenderCache1)
                    {
                        if (item != null)
                        {
                            Console.SetCursorPosition(0, item.Row);
                            Console.Write(new string(' ', Width));
                            Console.SetCursorPosition(item.Col, item.Row);
                            Console.Write(item.ContentString);
                        }
                    }
                    foreach (var item in RenderCache2)
                    {
                        if (item != null)
                        {
                            Console.SetCursorPosition(0, item.Row);
                            Console.Write(new string(' ', Width));
                            Console.SetCursorPosition(item.Col, item.Row);
                            Console.Write(item.ContentString);
                        }
                    }

                    isDisposing = false;
                }
            }
            isDisposed = true; // 标识此对象已释放
        }
    }
}
