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
            baseDir = new DirectoryInfo(baseDir).FullName;
            var source = new DirectoryInfo(sourceDir);
            if (!source.FullName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new ApplicationException("baseDir should contain sourceDir");
            var googleApi = await DriveServiceExt.Connect(_config);
            var targetId = await EnsureFolderTreeCreated(googleApi, targetDir);
            await UploadFolder(source, targetId, googleApi, baseDir, shouldCreateTarget: false);
            _logger.Info($"Copied {sourceDir} -> {targetDir} / {baseDir}");
        }

        private async Task UploadFolder(DirectoryInfo src, string parentTargetId, DriveService googleApi,
            string baseDir, bool shouldCreateTarget = true)
        {
            var stopwatch = Stopwatch.StartNew();
            var folderName = src.FullName.Length == baseDir.Length ? "\\" : src.FullName.Substring(baseDir.Length);
            try
            {
                var localFolder = await GetOrCreateLocalFolder(folderName);
                var targetId = shouldCreateTarget
                    ? (await googleApi.EnsureFolderCreated(parentTargetId, src.Name)).Id
                    : parentTargetId;
                if (localFolder.GDriveId != null && localFolder.GDriveId != targetId)
                {
                    throw new ApplicationException(
                        $"GDriveId for folder are different {localFolder.GDriveId} <> {targetId}");
                }

                if (localFolder.GDriveId == null)
                {
                    localFolder.GDriveId = targetId;
                    localFolder.UploadedAt = DateTime.Now;
                    await _db.Update(localFolder);
                }

                foreach (var page in src.EnumerateFiles().ChunkBy(_config.DegreeOfParallelism))
                    await Task.WhenAll(page.Select(file
                        => UploadFile(localFolder, file, googleApi, baseDir)));

                foreach (var dir in src.EnumerateDirectories())
                    await UploadFolder(dir, targetId, googleApi, baseDir);
                _logger.Info(folderName + " " + stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, folderName + " " + stopwatch.Elapsed);
            }
        }

        private async Task UploadFile(LocalFolder srcFolder, FileInfo srcFile, DriveService googleApi, string baseDir)
        {
            var stopwatch = Stopwatch.StartNew();
            var fileName = srcFile.FullName.Substring(baseDir.Length);
            try
            {
                var localFile = await GetOrCreateLocalFile(srcFolder, srcFile, fileName);
                if (localFile.GDriveId == null)
                {
                    using (var content = srcFile.OpenRead())
                    {
                        var gDriveFile = await googleApi.EnsureFileUploaded(srcFolder.GDriveId, srcFile.Name, content);
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