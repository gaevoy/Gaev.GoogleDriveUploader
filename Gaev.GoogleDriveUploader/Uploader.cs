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
using Serilog;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace Gaev.GoogleDriveUploader
{
    public class Uploader
    {
        private readonly IDatabase _db;
        private readonly ILogger _logger;
        private readonly Config _config;
        private readonly DriveService _googleApi;
        private readonly SemaphoreSlim _fileThrottler;
        private readonly SemaphoreSlim _googleThrottler;

        public Uploader(IDatabase db, ILogger logger, Config config, DriveService googleApi)
        {
            _db = db;
            _logger = logger;
            _config = config;
            _googleApi = googleApi;
            _fileThrottler = new SemaphoreSlim(Environment.ProcessorCount * 2);
            _googleThrottler = new SemaphoreSlim(config.DegreeOfParallelism);
        }

        public async Task Copy(string sourceDir, string targetDir, string baseDir = null, bool remainsOnly = false)
        {
            var stopwatch = Stopwatch.StartNew();
            baseDir = baseDir ?? sourceDir;
            sourceDir = Path.Combine(baseDir, sourceDir);
            baseDir = new DirectoryInfo(baseDir).FullName;
            var source = new DirectoryInfo(sourceDir);
            _logger.Information("{@input}", new
            {
                baseDir,
                sourceDir = source.FullName,
                targetDir,
                degreeOfParallelism = _config.DegreeOfParallelism,
                remainsOnly
            });
            if (!source.FullName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new ApplicationException("sourceDir should be inside baseDir");
            if (!source.Exists)
                throw new ApplicationException("source is not exist");

            _logger.Information($"Copying... {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir}");
            var targetId = await EnsureFolderTreeCreated(targetDir);
            var stat = new UploadingStatistic();
            await UploadFolder(source, targetId, baseDir, remainsOnly, stat, shouldCreateTargetFolder: false);
            _logger.Information(
                $"Copied {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir} {stopwatch.Elapsed}");
            if (stat.NumberOfFailedFolders > 0 || stat.NumberOfFailedFiles > 0)
                _logger.Warning("{@statistic}", new
                {
                    stat.NumberOfFailedFolders,
                    stat.NumberOfFailedFiles,
                    SizeUploaded = stat.SizeUploaded.FormatFileSize()
                });
            else
                _logger.Information("{@statistic}", new {SizeUploaded = stat.SizeUploaded.FormatFileSize()});
        }

        private async Task UploadFolder(
            DirectoryInfo src,
            string parentTargetId,
            string baseDir,
            bool remainsOnly,
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
                        _logger.Warning(targetFile.Name + " uploaded multiple times");
                    else
                        targetFiles[targetFile.Name] = targetFile;

                var sourceFiles = src.EnumerateFiles().ToList();
                if (sourceFiles.Count > 1000)
                    throw new NotImplementedException("Cannot upload more then 1000 files. Count: " +
                                                      sourceFiles.Count);

                var localFiles = localFolder.Files.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);

                await Task.WhenAll(sourceFiles.Select(file
                    => _fileThrottler.Throttle(()
                        => UploadFile(localFolder, localFiles, file, baseDir, targetFiles, remainsOnly, statistic))));

                foreach (var dir in src.EnumerateDirectories())
                    await UploadFolder(dir, targetId, baseDir, remainsOnly, statistic);

                _logger.Information(folderName + " " + stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, folderName + " " + stopwatch.Elapsed);
                lock (statistic)
                    statistic.NumberOfFailedFolders++;
            }
        }

        private async Task UploadFile(
            LocalFolder srcFolder,
            Dictionary<string, LocalFile> localFiles,
            FileInfo srcFile,
            string baseDir,
            Dictionary<string, GoogleFile> targetFiles,
            bool remainsOnly,
            UploadingStatistic statistic)
        {
            var stopwatch = Stopwatch.StartNew();
            var fileName = srcFile.FullName.Substring(baseDir.Length);
            long fileSize = 0;
            bool uploaded = true;
            LocalFile localFile = null;
            for (int probe = 1; probe <= 3; probe++)
                try
                {
                    uploaded = true;
                    var status = "skipped";
                    localFile = localFile ?? await GetOrCreateLocalFile(srcFolder, localFiles, fileName);
                    if (remainsOnly && localFile.GDriveId != null)
                        return;
                    var md5 = CalculateMd5(srcFile.FullName);
                    fileSize = srcFile.Length;
                    if (targetFiles.TryGetValue(srcFile.Name, out var targetFile))
                    {
                        if (targetFile.Md5Checksum != md5)
                        {
                            _logger.Warning(fileName + " uploaded file differs");
                            status = "different";
                            uploaded = false;
                        }
                        else if (localFile.GDriveId == null)
                        {
                            localFile.GDriveId = targetFile.Id;
                            localFile.Md5 = md5;
                            localFile.Size = fileSize;
                            localFile.UploadedAt = DateTime.Now;
                            await _db.Update(localFile);
                            status = "synced";
                        }
                    }
                    else
                    {
                        using (await _googleThrottler.Throttle())
                        {
                            var content = await ReadFile(srcFile);
                            targetFile = await _googleApi.UploadFile(srcFolder.GDriveId, srcFile.Name, content);
                            if (targetFile == null)
                                throw new ApplicationException(
                                    fileName + " file has not been uploaded, UploadFile returned null");
                        }

                        _logger.Debug(fileName + " uploaded as " + targetFile.Id);
                        localFile.GDriveId = targetFile.Id;
                        localFile.Md5 = md5;
                        localFile.Size = fileSize;
                        localFile.UploadedAt = DateTime.Now;
                        await _db.Update(localFile);
                        status = "uploaded";
                        lock (statistic)
                            statistic.SizeUploaded += localFile.Size;
                    }

                    _logger.Information(fileName + " {@more}",
                        new {status, duration = stopwatch.Elapsed, size = fileSize.FormatFileSize()});
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, fileName + " {@more}",
                        new {probe, duration = stopwatch.Elapsed, size = fileSize.FormatFileSize()});
                    uploaded = false;
                }

            if (!uploaded)
                lock (statistic)
                    statistic.NumberOfFailedFiles++;
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
            public int NumberOfFailedFiles { get; set; }
            public int NumberOfFailedFolders { get; set; }
            public long SizeUploaded { get; set; }
        }
    }
}