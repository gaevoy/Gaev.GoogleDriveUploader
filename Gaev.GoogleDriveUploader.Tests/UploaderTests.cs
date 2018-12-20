using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Gaev.GoogleDriveUploader.EntityFramework;
using Google.Apis.Drive.v3;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;

namespace Gaev.GoogleDriveUploader.Tests
{
    [TestFixture, NonParallelizable]
    public class UploaderTests
    {
        private static readonly Logger Logger = LogManager.GetLogger("GoogleDriveUploader");

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
            var uploader = new Uploader(new DbDatabase(), Logger, Config.ReadFromAppSettings());

            // When
            await uploader.Copy(sourceDir, sourceDir, "GDriveTest");

            // Then
            var gdrive = await GetGDriveTree(cli);
            Assert.That(gdrive["Alphabet"], Is.Not.Null);
            Assert.That((string)gdrive["Alphabet"]["a.txt"].content, Is.EqualTo(ToBase64String(files[0].content)));
            Assert.That((string)gdrive["Alphabet"]["B.txt"].content, Is.EqualTo(ToBase64String(files[1].content)));
            Assert.That((string)gdrive["Alphabet"]["Cc.txt"].content, Is.EqualTo(ToBase64String(files[2].content)));
        }

        private static string ToBase64String(string str)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(str));
        }

        private static async Task<dynamic> GetGDriveTree(DriveService cli)
        {
            async Task AddChildFiles(Dictionary<string, object> parent, string parentId)
            {
                foreach (var file in await cli.ListFoldersAndFiles(parentId))
                {
                    var isDir = file.MimeType == "application/vnd.google-apps.folder";
                    var child = new Dictionary<string, object>
                    {
                        {"dir", isDir},
                        {"md5", file.Md5Checksum}
                    };
                    if (isDir)
                        await AddChildFiles(child, file.Id);
                    else
                        child["content"] = Convert.ToBase64String(await cli.DownloadFile(file.Id));
                    parent[file.Name] = child;
                }
            }

            var testDir = await cli.EnsureFolderCreated("root", "GDriveTest");
            var root = new Dictionary<string, object>();
            await AddChildFiles(root, testDir.Id);
            var tree = JsonConvert.DeserializeObject<dynamic>(JsonConvert.SerializeObject(root));
            return tree;
        }


        private static async Task EnsureTestDirsCreated(DriveService cli)
        {
            var tempDir = new DirectoryInfo(GetTempDir());
            tempDir.Create();
            foreach (var file in tempDir.GetFiles())
                file.Delete();
            foreach (var dir in tempDir.GetDirectories())
                dir.Delete(true);
            var testDir = await cli.EnsureFolderCreated("root", "GDriveTest");
            foreach (var file in await cli.ListFoldersAndFiles(testDir.Id))
                await cli.Files.Delete(file.Id).ExecuteAsync();
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