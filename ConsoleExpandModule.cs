using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace module.dawnlc.me
{
    /// <summary>
    /// Console拓展 模块
    /// </summary>
    class ConsoleExpand : IDisposable
    {
        public class Render : IDisposable
        {
            public class Content
            {
                public int Row { get; set; }
                public int Col { get; set; }
                public bool Cover { get; set; }
                public string ContentString { get; set; }
                public Content(int row, int col, string content,bool cover = true)
                {
                    Row = row;
                    Col = col;
                    Cover = cover;
                    ContentString = content;
                }
            }
            public class RenderCache : ConcurrentQueue<Content>
            {
                public ConcurrentQueue<Content> Contents { get; set; }
                public bool CacheLock { get; set; }

                public object SyncRoot => ((ICollection)Contents).SyncRoot;

                public bool IsSynchronized => ((ICollection)Contents).IsSynchronized;

                public RenderCache()
                {
                    CacheLock = false;
                    Contents = new ConcurrentQueue<Content>();
                }
                public void Lock()
                {
                    CacheLock = true;
                }
                public void Unlock()
                {
                    CacheLock = false;
                }
            }
            private bool isDisposed = false;
            public int High = Console.WindowHeight;
            public int Width = Console.WindowWidth;
            public int Left = Console.CursorLeft;
            public int Top = Console.CursorTop;
            public Task RenderTask { get; set; }
            public CancellationTokenSource RenderTaskTokenSource { get; set; }
            public List<RenderCache> Pool { get; set; }
            public int RenderInterval { get; set; }
            public void Push(Content content)
            {
                try
                {
                    Pool.Where(p => !p.CacheLock).First().Enqueue(content);
                }
                catch (Exception ex)
                {
                    throw new Exception("缺少可用的输出缓存区！"+ex.ToString());
                }
            }
            public Render(int FPS = 10,int PoolCount = 2)
            {
                RenderInterval = 1000 / FPS;
                Pool = new List<RenderCache>();
                for (int i = 0; i < PoolCount; i++)
                {
                    Pool.Add(new RenderCache());
                }
                RenderTaskTokenSource = new CancellationTokenSource();
                RenderTask = Task.Factory.StartNew(() => {
                    try
                    {
                        while (true)
                        {
                            foreach (var item in Pool.Where(p => p.Count != 0))
                            {
                                item.Lock();
                                for (int i = 0; i < item.Count; i++)
                                {
                                    if (item.TryDequeue(out Content content))
                                    {
                                        if (content.Cover)
                                        {
                                            Console.SetCursorPosition(0, content.Row);
                                            Console.Write(new string(' ', Width));
                                        }
                                        Console.SetCursorPosition(content.Col, content.Row);
                                        Console.Write(content.ContentString);
                                    }
                                }
                                item.Unlock();
                            }
                            RenderTaskTokenSource.Token.ThrowIfCancellationRequested();
                            Thread.Sleep(RenderInterval);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        //外部取消
                        return;
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                }, RenderTaskTokenSource.Token);
            }
            public void Close()
            {
                Dispose(true);
            }
            ~Render()
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
                        //释放
                        RenderTaskTokenSource.Cancel();
                        RenderTask.Wait();
                        Console.SetCursorPosition(Left,Top);
                    }
                }
                isDisposed = true;
            }
        }

        private bool isDisposed = false;
        private Render RenderTask { get; set; }
        public ConsoleExpand()
        {
            RenderTask = new Render(25);
        }
        public void Print(int row, int col, string content)
        {
            RenderTask.Push(new Render.Content(col, row, content));
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
                    //释放
                    RenderTask.Close();
                }
            }
            isDisposed = true;
        }

    }
}
