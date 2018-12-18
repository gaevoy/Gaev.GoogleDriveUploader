using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Gaev.GoogleDriveUploader.EntityFramework;
using Google.Apis.Drive.v3;
using NLog;
using NUnit.Framework;

namespace Gaev.GoogleDriveUploader.Tests
{
    [TestFixture]
    public class UploaderTests
    {
        [Test]
        public async Task Test1()
        {
            var cli = await DriveServiceExt.Connect(Config.ReadFromAppSettings());
            var rootFolders = await cli.ListFolders();
        }

        [Test]
        public async Task Test2()
        {
            using (var db = new DbSession())
            {
                await db.Database.EnsureCreatedAsync();
                var all = db.Folders.ToList();
                db.Folders.Add(new LocalFolder {Name = Guid.NewGuid().ToString(), SeenAt = DateTime.Now});
                await db.SaveChangesAsync();
            }
        }

        [Test]
        public async Task It_should_upload_folder_Alphabet()
        {
            // Given
            var cli = await DriveServiceExt.Connect(Config.ReadFromAppSettings());
            await EnsureTestDirsCreated(cli);
            var sourceDir = GetTempDir();
            var aDir = Path.Combine(sourceDir, "Alphabet");
            Directory.CreateDirectory(aDir);
            var files = new[]
            {
                new {name = "a.txt", content = RandomString()},
                new {name = "B.txt", content = RandomString()},
                new {name = "Cc.txt", content = RandomString()}
            };
            foreach (var file in files)
                File.WriteAllText(Path.Combine(aDir, file.name), file.content);
            var uploader = new Uploader(new DbDatabase(), LogManager.GetLogger("GoogleDriveUploader"),
                Config.ReadFromAppSettings());

            // When
            await uploader.Copy(sourceDir, sourceDir, "GDriveTest");

            // Then
        }

        private static async Task EnsureTestDirsCreated(DriveService cli)
        {
            Directory.CreateDirectory(GetTempDir());
            var list = await cli.ListFolders(filter: "GDriveTest");
            if (!list.Any())
                await cli.CreateFolder("GDriveTest");
            using (var db = new DbSession())
            {
                db.Files.RemoveRange(db.Files.ToList());
                db.Folders.RemoveRange(db.Folders.ToList());
                db.Sessions.RemoveRange(db.Sessions.ToList());
                await db.SaveChangesAsync();
            }
        }

        private static string RandomString()
        {
            return string.Join("", Enumerable.Range(0, 20).Select(e => Guid.NewGuid().ToString("N")));
        }

        private static string GetTempDir()
        {
            return Path.Combine(Path.GetTempPath(), "GoogleDriveUploader");
        }
    }
}