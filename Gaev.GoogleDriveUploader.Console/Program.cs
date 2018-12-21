using System;
using System.Threading.Tasks;
using CommandLine;
using Gaev.GoogleDriveUploader.EntityFramework;
using NLog;

namespace Gaev.GoogleDriveUploader.Console
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var result = Parser.Default.ParseArguments<UploadOptions>(args);
            var opt = (result as Parsed<UploadOptions>)?.Value;
            if (opt == null)
                return;

            var logger = LogManager.GetLogger("GoogleDriveUploader");
            try
            {
                var config = Config.ReadFromAppSettings();
                config.DegreeOfParallelism = opt.DegreeOfParallelism ?? config.DegreeOfParallelism;
                var db = new DbDatabase();
                await db.EnsureCreated();
                var googleApi = await DriveServiceExt.Connect(config);
                var uploader = new Uploader(db, logger, config, googleApi);
                await uploader.Copy(
                    opt.SourceDir,
                    opt.TargetDir,
                    opt.BaseDir ?? Environment.CurrentDirectory);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }
    }

    [Verb("upload", HelpText = "Upload local folder to Google Drive")]
    public class UploadOptions
    {
        [Option("source", Required = true, HelpText = "File system source directory")]
        public string SourceDir { get; set; }

        [Option("target", Required = true, HelpText = "Google Drive target directory")]
        public string TargetDir { get; set; }

        [Option("base", Required = false, HelpText = "File system base directory (default: working directory)")]
        public string BaseDir { get; set; }

        [Option("degreeOfParallelism", Required = false, HelpText = "Degree of parallelism during file upload")]
        public int? DegreeOfParallelism { get; set; }
    }
}