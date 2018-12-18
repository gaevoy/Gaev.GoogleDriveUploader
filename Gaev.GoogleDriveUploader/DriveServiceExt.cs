using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.EntityFramework;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;

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

        public static async Task<File> CreateFolder(
            this DriveService cli,
            string name,
            string parentId = "root")
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