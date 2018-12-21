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
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace Gaev.GoogleDriveUploader
{
    public class Uploader
    {
        private readonly IDatabase _db;
        private readonly ILogger _logger;
        private readonly Config _config;
        private readonly DriveService _googleApi;

        public Uploader(IDatabase db, ILogger logger, Config config, DriveService googleApi)
        {
            _db = db;
            _logger = logger;
            _config = config;
            _googleApi = googleApi;
        }

        public async Task Copy(string sourceDir, string targetDir, string baseDir = null)
        {
            var stopwatch = Stopwatch.StartNew();
            baseDir = baseDir ?? sourceDir;
            sourceDir = Path.Combine(baseDir, sourceDir);
            baseDir = new DirectoryInfo(baseDir).FullName;
            var source = new DirectoryInfo(sourceDir);
            _logger.Info($"baseDir: {baseDir} | sourceDir: {source.FullName} | targetDir: {targetDir} | degreeOfParallelism: {_config.DegreeOfParallelism}");
            if (!source.FullName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new ApplicationException("sourceDir should be inside baseDir");
            if (!source.Exists)
                throw new ApplicationException("source is not exist");

            _logger.Info($"Copying... {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir}");
            var targetId = await EnsureFolderTreeCreated(targetDir);
            await UploadFolder(source, targetId, baseDir, shouldCreateTargetFolder: false);
            _logger.Info(
                $"Copied {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir} {stopwatch.Elapsed}");
        }

        private async Task UploadFolder(
            DirectoryInfo src,
            string parentTargetId,
            string baseDir,
            bool shouldCreateTargetFolder = true)
        {
            var stopwatch = Stopwatch.StartNew();
            var folderName = GetShortFolderName(baseDir, src.FullName);
            try
            {
                var localFolder = await GetOrCreateLocalFolder(folderName);
                var targetId = shouldCreateTargetFolder
                    ? (await _googleApi.EnsureFolderCreated(parentTargetId, src.Name)).Id
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

                var targetFiles = (await _googleApi.ListFiles(targetId))
                    .ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

                var sourceFiles = src.EnumerateFiles().ToList();
                if (sourceFiles.Count > 1000)
                    throw new NotImplementedException("Cannot upload more then 1000 files. Count: " +
                                                      sourceFiles.Count);
                foreach (var page in sourceFiles.ChunkBy(_config.DegreeOfParallelism))
                    await Task.WhenAll(page.Select(file
                        => UploadFile(localFolder, file, baseDir, targetFiles)));

                foreach (var dir in src.EnumerateDirectories())
                    await UploadFolder(dir, targetId, baseDir);

                _logger.Info(folderName + " " + stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, folderName + " " + stopwatch.Elapsed);
            }
        }

        private async Task UploadFile(
            LocalFolder srcFolder,
            FileInfo srcFile,
            string baseDir,
            Dictionary<string, GoogleFile> targetFiles)
        {
            var stopwatch = Stopwatch.StartNew();
            var fileName = srcFile.FullName.Substring(baseDir.Length);
            for (int probe = 1; probe <= 3; probe++)
                try
                {
                    var localFile = await GetOrCreateLocalFile(srcFolder, fileName);
                    var md5 = CalculateMd5(srcFile.FullName);
                    if (targetFiles.TryGetValue(srcFile.Name, out var targetFile))
                    {
                        if (targetFile.Md5Checksum != md5)
                            _logger.Warn(fileName + " uploaded file differs");
                        else if (localFile.GDriveId == null)
                        {
                            localFile.GDriveId = targetFile.Id;
                            localFile.Md5 = md5;
                            localFile.Size = srcFile.Length;
                            localFile.UploadedAt = DateTime.Now;
                        }
                    }
                    else
                    {
                        using (var content = srcFile.OpenRead())
                            targetFile = await _googleApi.UploadFile(srcFolder.GDriveId, srcFile.Name, content);
                        _logger.Trace(fileName + " uploaded as " + targetFile.Id);

                        localFile.GDriveId = targetFile.Id;
                        localFile.Md5 = md5;
                        localFile.Size = srcFile.Length;
                        localFile.UploadedAt = DateTime.Now;
                    }

                    await _db.Update(localFile);
                    _logger.Info(fileName + " " + stopwatch.Elapsed);
                    break;
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

        private async Task<string> EnsureFolderTreeCreated(string path)
        {
            var dirs = path.Split('/', '\\').Where(e => !string.IsNullOrWhiteSpace(e));
            var gId = "root";
            foreach (var dir in dirs)
                gId = (await _googleApi.EnsureFolderCreated(gId, dir)).Id;
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

        private async Task<LocalFile> GetOrCreateLocalFile(LocalFolder localFolder, string fileName)
        {
            var localFile = localFolder.Files.FirstOrDefault(e =>
                string.Compare(e.Name, fileName, StringComparison.OrdinalIgnoreCase) == 0);
            if (localFile == null)
            {
                localFile = new LocalFile
                {
                    Name = fileName,
                    Folder = localFolder,
                    SeenAt = DateTime.Now
                };
                await _db.Insert(localFile);
            }

            return localFile;
        }

        private static string GetShortFolderName(string baseDir, string srcDir)
        {
            return srcDir.Length == baseDir.Length ? "\\" : srcDir.Substring(baseDir.Length);
        }
    }
}