using System;
using NLog;

namespace Gaev.GoogleDriveUploader.Console
{
    class Program
    {
        static void Main(string[] args)
        {
            var logger = LogManager.GetLogger("GoogleDriveUploader");
            logger.Info("Hello World!");
        }
    }
}