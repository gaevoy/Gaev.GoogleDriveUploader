using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
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
            var gParentDirs = targetDir.Split('/', '\\').Where(e => !string.IsNullOrWhiteSpace(e));
            var gParentDirId = "root";
            foreach (var gDir in gParentDirs)
            {
                gParentDirId = (await cli.EnsureFolderCreated(gParentDirId, gDir)).Id;
            }

            _logger.Trace("TargetDirId " + gParentDirId);
            foreach (var child in source.GetDirectories())
            {
                var folderName = child.FullName.Substring(baseDir.Length + 1);
                _logger.Info(folderName);
                var folder = await GetOrCreateDbFolder(folderName);
                var gDriveId = (await cli.EnsureFolderCreated(gParentDirId, child.Name)).Id;
                _logger.Trace("GDriveId " + gDriveId);
                if (folder.GDriveId != null && folder.GDriveId != gDriveId)
                {
                    throw new ApplicationException($"GDriveId for folder are different {folder.GDriveId} <> {gDriveId}");
                }

                if (folder.GDriveId == null)
                {
                    folder.GDriveId = gDriveId;
                    folder.UploadedAt = DateTime.Now;
                    await _db.Update(folder);
                }


                var _ = folder;
            }
        }

        private async Task<LocalFolder> GetOrCreateDbFolder(string name)
        {
            name = name.ToLower();
            var folder = await _db.GetFolder(name);
            if (folder == null)
            {
                folder = new LocalFolder
                {
                    Name = name,
                    SeenAt = DateTime.Now
                };
                await _db.Insert(folder);
            }

            return folder;
        }
    }
}