using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        public async Task Copy(string sourceDir, string targetDir, string baseDir = null, UploadingContext ctx = null)
        {
            ctx = ctx ?? new UploadingContext();

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
                ctx.RemainsOnly,
                ctx.EstimateOnly
            });
            if (!source.FullName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new ApplicationException("sourceDir should be inside baseDir");
            if (!source.Exists)
                throw new ApplicationException("source is not exist");

            _logger.Information($"Copying... {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir}");
            var target = await EnsureFolderTreeCreated(targetDir);
            var folder = new FolderSyncJob(source, target);
            await UploadFolder(null, folder, baseDir, ctx);
            _logger.Information(
                $"Copied {baseDir}: {GetShortFolderName(baseDir, source.FullName)} -> {targetDir} {stopwatch.Elapsed}");
            if (ctx.NumberOfFailedFolders > 0 || ctx.NumberOfFailedFiles > 0)
                _logger.Warning("{@statistic}", new
                {
                    ctx.NumberOfFailedFolders,
                    ctx.NumberOfFailedFiles,
                    SizeUploaded = ctx.SizeUploaded.FormatFileSize()
                });
            else
                _logger.Information("{@statistic}", new
                {
                    SizeUploaded = ctx.SizeUploaded.FormatFileSize(),
                    cancelled = ctx.Cancellation.IsCancellationRequested
                });
        }

        private async Task UploadFolder(
            FolderSyncJob parent,
            FolderSyncJob folder,
            string baseDir,
            UploadingContext ctx)
        {
            if (ctx.Cancellation.IsCancellationRequested) return;

            var stopwatch = Stopwatch.StartNew();
            var folderName = GetShortFolderName(baseDir, folder.Source.FullName);
            try
            {
                await folder.Initialize(_db, folderName);
                if (folder.Target == null)
                {
                    await folder.Upload(_db, _googleApi, parent.Target.Id);
                    await folder.UpdateDb(_db);
                }
                else if (folder.DbFolder.GDriveId == null)
                    await folder.UpdateDb(_db);
                else if (folder.Target.Id != folder.DbFolder.GDriveId)
                    throw new ApplicationException(
                        $"GDriveId for folder are different {folder.DbFolder.GDriveId} <> {folder.Target.Id}");

                if (ctx.Cancellation.IsCancellationRequested) return;
                await folder.LoadChildren(_googleApi, _logger);

                if (ctx.Cancellation.IsCancellationRequested) return;
                var localFiles =
                    folder.DbFolder.Files.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
                await Task.WhenAll(folder.Files.Select(file
                    => _fileThrottler.Throttle(()
                        => UploadFile(folder.DbFolder, localFiles, file, baseDir, ctx))));

                if (ctx.Cancellation.IsCancellationRequested) return;
                foreach (var child in folder.Folders)
                    await UploadFolder(folder, child, baseDir, ctx);

                if (ctx.Cancellation.IsCancellationRequested) return;
                _logger.Information(folderName + " " + stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Error(ex, folderName + " " + stopwatch.Elapsed);
                lock (ctx)
                    ctx.NumberOfFailedFolders++;
            }
        }

        private async Task UploadFile(
            LocalFolder srcFolder,
            Dictionary<string, LocalFile> localFiles,
            FileSyncJob file,
            string baseDir,
            UploadingContext ctx)
        {
            if (ctx.Cancellation.IsCancellationRequested) return;

            var stopwatch = Stopwatch.StartNew();
            var fileName = file.Source.FullName.Substring(baseDir.Length);
            bool uploaded = true;
            for (int probe = 1; probe <= 3; probe++)
                try
                {
                    uploaded = true;
                    var status = "skipped";
                    await file.Initialize(_db, srcFolder, localFiles, fileName);
                    if ((ctx.RemainsOnly || ctx.EstimateOnly) && file.DbFile.GDriveId != null)
                        return;
                    if (file.Target != null)
                    {
                        if (file.Target.Md5Checksum != file.Md5)
                        {
                            _logger.Warning(fileName + " uploaded file differs");
                            status = "different";
                            uploaded = false;
                        }
                        else if (file.DbFile.GDriveId == null)
                        {
                            await file.UpdateDb(_db);
                            status = "synced";
                        }
                    }
                    else
                    {
                        if (ctx.EstimateOnly)
                            status = "to upload";
                        else
                        {
                            using (await _googleThrottler.Throttle())
                                await file.Upload(_googleApi, ctx.Cancellation);
                            _logger.Debug(fileName + " uploaded as " + file.Target.Id);
                            await file.UpdateDb(_db);
                            status = "uploaded";
                        }

                        lock (ctx)
                            ctx.SizeUploaded += file.Size;
                    }

                    _logger.Information(fileName + " {@more}",
                        new {status, duration = stopwatch.Elapsed, size = file.Size.FormatFileSize()});
                    break;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, fileName + " {@more}",
                        new {probe, duration = stopwatch.Elapsed, size = file.Size.FormatFileSize()});
                    uploaded = false;
                }

            if (!uploaded)
                lock (ctx)
                    ctx.NumberOfFailedFiles++;
        }

        private async Task<GoogleFile> EnsureFolderTreeCreated(string path)
        {
            var dirs = path.Split('/', '\\').Where(e => !string.IsNullOrWhiteSpace(e));
            var folder = new GoogleFile {Id = "root"};
            foreach (var dir in dirs)
                folder = await _googleApi.EnsureFolderCreated(folder.Id, dir);
            return folder;
        }

        private static string GetShortFolderName(string baseDir, string srcDir)
        {
            return srcDir.Length == baseDir.Length ? "\\" : srcDir.Substring(baseDir.Length);
        }
    }
}