using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Google.Apis.Drive.v3;
using NLog;

namespace Gaev.GoogleDriveUploader
{
    public class Uploader
    {
        private readonly IDatabase _db;
        private readonly ILogger _logger;
        private readonly Config _config;

        public Uploader(IDatabase db, ILogger logger, Config config)
        {
            _db = db;
            _logger = logger;
            _config = config;
        }

        public async Task Copy(string baseDir, string sourceDir, string targetDir)
        {
            _logger.Info($"Copying... {sourceDir} -> {targetDir} / {baseDir}");
            baseDir = new DirectoryInfo(baseDir).FullName.ToLower();
            var source = new DirectoryInfo(sourceDir);
            if (!source.FullName.ToLower().StartsWith(baseDir))
                throw new ApplicationException("baseDir should contain sourceDir");
            var cli = await DriveServiceExt.Connect(_config);
            var targetGDriveId = await EnsureFolderTreeCreated(cli, targetDir);

            _logger.Trace("TargetDirId " + targetGDriveId);
            foreach (var child in source.GetDirectories())
            {
                var folderName = child.FullName.Substring(baseDir.Length + 1);
                _logger.Info(folderName);
                var localFolder = await GetOrCreateLocalFolder(folderName);
                var gDriveId = (await cli.EnsureFolderCreated(targetGDriveId, child.Name)).Id;
                _logger.Trace("GDriveId " + gDriveId);
                if (localFolder.GDriveId != null && localFolder.GDriveId != gDriveId)
                {
                    throw new ApplicationException(
                        $"GDriveId for folder are different {localFolder.GDriveId} <> {gDriveId}");
                }

                if (localFolder.GDriveId == null)
                {
                    localFolder.GDriveId = gDriveId;
                    localFolder.UploadedAt = DateTime.Now;
                    await _db.Update(localFolder);
                }

                var files = child.EnumerateFiles().ToList();

                var _ = localFolder;
                foreach (var page in files.ChunkBy(_config.DegreeOfParallelism))
                    await Task.WhenAll(page.Select(file
                        => UploadFile(localFolder, file, cli, baseDir)));
            }

            _logger.Info($"Copied {sourceDir} -> {targetDir} / {baseDir}");
        }

        private async Task UploadFile(LocalFolder localFolder, FileInfo file, DriveService cli, string baseDir)
        {
            var stopwatch = Stopwatch.StartNew();
            var fileName = file.FullName.Substring(baseDir.Length + 1);
            try
            {
                var localFile = await GetOrCreateLocalFile(localFolder, file, fileName);
                if (localFile.GDriveId == null)
                {
                    using (var content = file.OpenRead())
                    {
                        var gDriveFile = await cli.EnsureFileUploaded(localFolder.GDriveId, file.Name, content);
                        localFile.GDriveId = gDriveFile.Id;
                    }

                    localFile.UploadedAt = DateTime.Now;
                }

                await _db.Update(localFile);
                _logger.Info(fileName + " " + stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, fileName + " " + stopwatch.Elapsed);
            }
        }

        private static string CalculateMd5(string filename)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filename))
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
        }

        private static async Task<string> EnsureFolderTreeCreated(DriveService cli, string path)
        {
            var dirs = path.Split('/', '\\').Where(e => !string.IsNullOrWhiteSpace(e));
            var gId = "root";
            foreach (var dir in dirs)
                gId = (await cli.EnsureFolderCreated(gId, dir)).Id;
            return gId;
        }

        private async Task<LocalFolder> GetOrCreateLocalFolder(string name)
        {
            var folder = await _db.GetFolder(name);
            if (folder == null)
            {
                folder = new LocalFolder
                {
                    Name = name,
                    SeenAt = DateTime.Now,
                    Files = new List<LocalFile>()
                };
                await _db.Insert(folder);
            }

            return folder;
        }

        private async Task<LocalFile> GetOrCreateLocalFile(LocalFolder localFolder, FileInfo file, string fileName)
        {
            var localFile = localFolder.Files.FirstOrDefault(e =>
                string.Compare(e.Name, fileName, StringComparison.OrdinalIgnoreCase) == 0);
            if (localFile == null)
            {
                localFile = new LocalFile
                {
                    Name = fileName,
                    Folder = localFolder,
                    SeenAt = DateTime.Now,
                    Size = file.Length,
                    Md5 = CalculateMd5(file.FullName)
                };
                await _db.Insert(localFile);
            }

            return localFile;
        }
    }
}