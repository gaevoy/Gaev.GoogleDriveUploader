using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.EntityFramework;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore.Storage.Internal;

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
            return (await req.ExecuteAsync()).Files;
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

        public static async Task<File> EnsureFolderCreated(this DriveService cli,
            string parentId,
            string name)
        {
            var list = await cli.ListFolders(parentId, name);
            if (!list.Any())
                return await cli.CreateFolder(parentId, name);
            return list.First();
        }

        public static async Task<DriveService> Connect(Config config)
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
                CancellationToken.None,
                new SqliteStore());

            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "GoogleDriveUploader"
            });
        }
    }
}