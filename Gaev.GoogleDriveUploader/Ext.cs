using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gaev.GoogleDriveUploader
{
    public static class Ext
    {
        public static string FormatFileSize(this long fileSize)
        {
            string[] sizes = {"B", "KB", "MB", "GB", "TB"};
            double len = fileSize;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##}{sizes[order]}";
        }

        public static async Task<IDisposable> Throttle(this SemaphoreSlim throttler)
        {
            await Task.Yield();
            await throttler.WaitAsync();
            return new Throttler(throttler);
        }      
        
        public static async Task Throttle(this SemaphoreSlim throttler, Func<Task> act)
        {
            using (await throttler.Throttle())
                await act();
        }

        private class Throttler : IDisposable
        {
            private readonly SemaphoreSlim _throttler;

            public Throttler(SemaphoreSlim throttler)
            {
                _throttler = throttler;
            }

            public void Dispose()
            {
                _throttler.Release();
            }
        }
    }
}