using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Drive.v3;
using Serilog;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class FolderSyncJob
    {
        public DirectoryInfo Source { get; }
        public LocalFolder DbFolder { get; private set; }
        public GoogleFile Target { get; private set; }
        public List<FileSyncJob> Files { get; private set; }
        public List<FolderSyncJob> Folders { get; private set; }

        public FolderSyncJob(DirectoryInfo source, GoogleFile target)
        {
            Source = source;
            Target = target;
        }

        public async Task Initialize(IDatabase db, string relativeName)
        {
            relativeName = relativeName.ToLower();
            var dbFolder = await db.GetFolder(relativeName);
            if (dbFolder == null)
            {
                dbFolder = new LocalFolder
                {
                    Name = relativeName,
                    SeenAt = DateTime.Now,
                    Files = new List<LocalFile>()
                };
                await db.Insert(dbFolder);
            }

            DbFolder = dbFolder;
        }

        public async Task Upload(IDatabase db, DriveService googleApi, string parentTargetId)
        {
            Target = await googleApi.EnsureFolderCreated(parentTargetId, Source.Name);
            if (DbFolder.GDriveId != null && DbFolder.GDriveId != Target.Id)
                throw new ApplicationException($"GDriveId for folder are different {DbFolder.GDriveId} <> {Target.Id}");
        }
        
        public async Task UpdateDb(IDatabase db)
        {
            DbFolder.GDriveId = Target.Id;
            DbFolder.UploadedAt = DateTime.Now;
            await db.Update(DbFolder);
        }

        public async Task LoadChildren(DriveService googleApi, ILogger logger)
        {
            var targets = new Dictionary<string, GoogleFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in await googleApi.ListFoldersAndFiles(Target.Id))
                if (targets.ContainsKey(target.Name))
                    logger.Warning(Path.Combine(DbFolder.Name, target.Name) + " uploaded multiple times");
                else
                    targets[target.Name] = target;

            Files = Source.EnumerateFiles().Select(source =>
            {
                targets.TryGetValue(source.Name, out var target);
                return new FileSyncJob(source, target);
            }).ToList();

            Folders = Source.EnumerateDirectories().Select(source =>
            {
                targets.TryGetValue(source.Name, out var target);
                return new FolderSyncJob(source, target);
            }).ToList();
        }
    }
}