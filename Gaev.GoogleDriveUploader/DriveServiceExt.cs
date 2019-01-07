using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.EntityFramework;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using File = Google.Apis.Drive.v3.Data.File;

namespace Gaev.GoogleDriveUploader
{
    public static class DriveServiceExt
    {
        public static async Task<IList<File>> ListFolders(
            this DriveService cli,
            string parentId = "root",
            string filter = null)
        {
            var req = cli.Files.List();
            req.Q = $"'{parentId}' in parents and mimeType='application/vnd.google-apps.folder' and trashed=false";
            if (filter != null)
                req.Q += $" and name='{filter}'";
            req.PageSize = 1000;
            return (await req.ExecuteAsync()).Files;
        }

        public static async Task<IList<File>> ListFoldersAndFiles(
            this DriveService cli,
            string parentId = "root",
            int pageSize = 1000)
        {
            var files = new List<File>();
            Google.Apis.Drive.v3.Data.FileList prev = null;
            do
            {
                var req = cli.Files.List();
                req.Q = $"'{parentId}' in parents";
                req.PageSize = pageSize;
                req.Fields = "nextPageToken, files(id, name, mimeType, md5Checksum, size)";
                if (prev != null)
                    req.PageToken = prev.NextPageToken;
                prev = await req.ExecuteAsync();
                files.AddRange(prev.Files);
            } while (!string.IsNullOrWhiteSpace(prev.NextPageToken));

            return files;
        }

        public static async Task<File> CreateFolder(this DriveService cli,
            string parentId,
            string name)
        {
            var fileMetadata = new File
            {
                Name = name,
                Parents = new[] {parentId},
                MimeType = "application/vnd.google-apps.folder"
            };
            var req = cli.Files.Create(fileMetadata);
            req.Fields = "id";
            return await req.ExecuteAsync();
        }

        public static async Task<File> UploadFile(this DriveService cli,
            string parentId,
            string name,
            Stream content,
            CancellationToken cancellation)
        {
            var fileMetadata = new File
            {
                Name = name,
                Parents = new[] {parentId}
            };
            var req = cli.Files.Create(fileMetadata, content, MimeTypeMap.GetMimeType(Path.GetExtension(name)));
            req.Fields = "id";
            await req.UploadAsync(cancellation);
            return req.ResponseBody;
        }

        public static async Task<byte[]> DownloadFile(this DriveService cli, string id)
        {
            var ms = new MemoryStream();
            await cli.Files.Get(id).DownloadAsync(ms);
            return ms.ToArray();
        }

        public static async Task<File> EnsureFolderCreated(this DriveService cli,
            string parentId,
            string name)
        {
            var list = await cli.ListFolders(parentId, name);
            if (!list.Any())
                return await cli.CreateFolder(parentId, name);
            return list.First();
        }

        public static async Task<File> EnsureFileUploaded(this DriveService cli,
            string parentId,
            string name,
            Stream content)
        {
            var list = await cli.ListFolders(parentId, name);
            if (!list.Any())
                return await cli.UploadFile(parentId, name, content, CancellationToken.None);
            return list.First();
        }

        public static async Task<DriveService> Connect(Config config, CancellationToken cancellation)
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = config.ClientId,
                    ClientSecret = config.ClientSecret
                },
                new[]
                {
                    DriveService.Scope.Drive,
                    DriveService.Scope.DriveFile
                },
                Environment.UserName,
                cancellation,
                new SqliteStore());

            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "GoogleDriveUploader",
                HttpClientFactory = new UploaderHttpClientFactory(),
                GZipEnabled = false
            });
        }
    }
}