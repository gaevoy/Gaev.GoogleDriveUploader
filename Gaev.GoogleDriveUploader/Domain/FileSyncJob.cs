using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Drive.v3;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class FileSyncJob
    {
        private string _md5;
        private long? _size;
        private string _targetGDriveId;

        public FileSyncJob(FileInfo source, GoogleFile target)
        {
            Source = source;
            Target = target;
        }

        public FileInfo Source { get; }
        public LocalFile DbFile { get; private set; }
        public GoogleFile Target { get; private set; }

        public string Md5 => _md5 ?? (_md5 = CalculateMd5());
        public long Size => (_size ?? (_size = Source.Length)).Value;

        public string RelativeName => DbFile.Name;

        public async Task Initialize(IDatabase db,
            LocalFolder localFolder,
            Dictionary<string, LocalFile> localFiles,
            string relativeName)
        {
            _targetGDriveId = localFolder.GDriveId;
            if (!localFiles.TryGetValue(relativeName, out var dbFile))
            {
                dbFile = new LocalFile
                {
                    Name = relativeName,
                    FolderName = localFolder.Name,
                    SeenAt = DateTime.Now
                };
                await db.Insert(dbFile);
            }

            DbFile = dbFile;
        }

        public async Task UpdateDb(IDatabase db)
        {
            DbFile.GDriveId = Target.Id;
            DbFile.Md5 = Md5;
            DbFile.Size = Source.Length;
            DbFile.UploadedAt = DateTime.Now;
            await db.Update(DbFile);
        }

        public async Task Upload(DriveService googleApi, CancellationToken cancellation)
        {
            // TODO: Read file size, MD5 and upload at once
            using (var content = Source.OpenRead())
                Target = await googleApi.UploadFile(_targetGDriveId, Source.Name, content, cancellation);
            if (Target == null)
                throw new ApplicationException("File has not been uploaded, UploadFile returned null");
        }

        private string CalculateMd5()
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(Source.FullName))
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
        }
    }
}