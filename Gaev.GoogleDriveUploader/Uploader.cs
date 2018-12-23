using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Google.Apis.Drive.v3;
using Newtonsoft.Json;
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
        private readonly SemaphoreSlim _throttler;

        public Uploader(IDatabase db, ILogger logger, Config config, DriveService googleApi)
        {
            _db = db;
            _logger = logger;
            _config = config;
            _googleApi = googleApi;
            _throttler = new SemaphoreSlim(config.DegreeOfParallelism);
        }

        public async Task Copy(string sourceDir, string targetDir, string baseDir = null)
        {
            var stopwatch = Stopwatch.StartNew();
            baseDir = baseDir ?? sourceDir;
            sourceDir = Path.Combine(baseDir, sourceDir);
            baseDir = new DirectoryInfo(baseDir).FullName;
            var source = new DirectoryInfo(sourceDir);
            _logger.Info(
                $"baseDir: {baseDir} | sourceDir: {source.FullName} | targetDir: {targetDir} | degreeOfParallelism: {_config.DegreeOfParallelism}");
            if (!source.FullName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new ApplicationException("sourceDir should be inside baseDir");
            if (!source.Exists)
                throw new ApplicationException("source is not exist");

            _logger.Info($"Copying... {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir}");
            var targetId = await EnsureFolderTreeCreated(targetDir);
            var statistic = new UploadingStatistic();
            await UploadFolder(source, targetId, baseDir, statistic, shouldCreateTargetFolder: false);
            _logger.Info(
                $"Copied {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir} {stopwatch.Elapsed}");
            var jsonStatistic = JsonConvert.SerializeObject(statistic);
            if (statistic.NumberOfFailedUploads > 0)
                _logger.Warn(jsonStatistic);
            else
                _logger.Info(jsonStatistic);
        }

        private async Task UploadFolder(
            DirectoryInfo src,
            string parentTargetId,
            string baseDir,
            UploadingStatistic statistic,
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

                var targetFiles = new Dictionary<string, GoogleFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var targetFile in await _googleApi.ListFiles(targetId))
                    if (targetFiles.ContainsKey(targetFile.Name))
                        _logger.Warn(targetFile.Name + " uploaded multiple times");
                    else
                        targetFiles[targetFile.Name] = targetFile;

                var sourceFiles = src.EnumerateFiles().ToList();
                if (sourceFiles.Count > 1000)
                    throw new NotImplementedException("Cannot upload more then 1000 files. Count: " +
                                                      sourceFiles.Count);

                var localFiles = localFolder.Files.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

                await Task.WhenAll(sourceFiles.Select(file
                    => Throttle(()
                        => UploadFile(localFolder, localFiles, file, baseDir, targetFiles, statistic))));

                foreach (var dir in src.EnumerateDirectories())
                    await UploadFolder(dir, targetId, baseDir, statistic);

                _logger.Info(folderName + " " + stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, folderName + " " + stopwatch.Elapsed);
            }
        }

        private async Task UploadFile(
            LocalFolder srcFolder,
            Dictionary<string, LocalFile> localFiles,
            FileInfo srcFile,
            string baseDir,
            Dictionary<string, GoogleFile> targetFiles,
            UploadingStatistic statistic)
        {
            var stopwatch = Stopwatch.StartNew();
            var fileName = srcFile.FullName.Substring(baseDir.Length);
            bool uploaded = true;
            LocalFile localFile = null;
            for (int probe = 1; probe <= 3; probe++)
                try
                {
                    uploaded = true;
                    var status = "skipped";
                    localFile = localFile ?? await GetOrCreateLocalFile(srcFolder, localFiles, fileName);
                    var md5 = CalculateMd5(srcFile.FullName);
                    if (targetFiles.TryGetValue(srcFile.Name, out var targetFile))
                    {
                        if (targetFile.Md5Checksum != md5)
                        {
                            _logger.Warn(fileName + " uploaded file differs");
                            status = "different";
                            uploaded = false;
                        }
                        else if (localFile.GDriveId == null)
                        {
                            localFile.GDriveId = targetFile.Id;
                            localFile.Md5 = md5;
                            localFile.Size = srcFile.Length;
                            localFile.UploadedAt = DateTime.Now;
                            await _db.Update(localFile);
                            status = "synced";
                        }
                    }
                    else
                    {
                        var content = await ReadFile(srcFile);
                        targetFile = await _googleApi.UploadFile(srcFolder.GDriveId, srcFile.Name, content);
                        if (targetFile == null)
                            throw new ApplicationException(
                                fileName + " file has not been uploaded, UploadFile returned null");
                        _logger.Trace(fileName + " uploaded as " + targetFile.Id);

                        localFile.GDriveId = targetFile.Id;
                        localFile.Md5 = md5;
                        localFile.Size = srcFile.Length;
                        localFile.UploadedAt = DateTime.Now;
                        await _db.Update(localFile);
                        status = "uploaded";
                        lock (statistic)
                            statistic.SizeUploaded += localFile.Size;
                    }

                    _logger.Info(fileName + " " + status + " " + stopwatch.Elapsed);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, fileName + " " + stopwatch.Elapsed + " probe: " + probe);
                    uploaded = false;
                }

            if (!uploaded)
                lock (statistic)
                    statistic.NumberOfFailedUploads++;
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
            name = name.ToLower();
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

        private async Task<LocalFile> GetOrCreateLocalFile(
            LocalFolder localFolder,
            Dictionary<string, LocalFile> localFiles, string fileName)
        {
            if (!localFiles.TryGetValue(fileName, out var localFile))
            {
                localFile = new LocalFile
                {
                    Name = fileName,
                    FolderName = localFolder.Name,
                    SeenAt = DateTime.Now
                };
                await _db.Insert(localFile);
            }

            return localFile;
        }

        private async Task Throttle(Func<Task> act)
        {
            await Task.Yield();
            await _throttler.WaitAsync();
            try
            {
                await act();
            }
            finally
            {
                _throttler.Release();
            }
        }

        private static string GetShortFolderName(string baseDir, string srcDir)
        {
            return srcDir.Length == baseDir.Length ? "\\" : srcDir.Substring(baseDir.Length);
        }

        private static async Task<MemoryStream> ReadFile(FileInfo srcFile)
        {
            // https://github.com/googleapis/google-api-dotnet-client/issues/833#issuecomment-362898041
            var content = new MemoryStream();
            using (var stream = srcFile.OpenRead())
                await stream.CopyToAsync(content);
            return content;
        }

        class UploadingStatistic
        {
            public int NumberOfFailedUploads { get; set; }
            public long SizeUploaded { get; set; }
        }
    }
}