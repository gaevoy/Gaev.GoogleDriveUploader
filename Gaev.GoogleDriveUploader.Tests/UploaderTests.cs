using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Xunit;

namespace Gaev.GoogleDriveUploader.Tests
{
    public class UploaderTests
    {
        [Fact]
        public async Task Test1()
        {
            var cli = await NewDriveService();
            var rootFolders = await cli.ListRootFolders();
        }

        private static async Task<DriveService> NewDriveService()
        {
            var credential = await ReadGoogleCredential();
            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Drive API Sample"
            });
        }

        private static async Task<ICredential> ReadGoogleCredential()
        {
            var cfg = Config.ReadFromAppSettings();
            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = cfg.ClientId,
                    ClientSecret = cfg.ClientSecret
                },
                new[]
                {
                    DriveService.Scope.Drive,
                    DriveService.Scope.DriveFile
                },
                Environment.UserName,
                CancellationToken.None,
                new FileDataStore("Gaev.GoogleDriveUploader.Auth.Store"));
        }
    }
}