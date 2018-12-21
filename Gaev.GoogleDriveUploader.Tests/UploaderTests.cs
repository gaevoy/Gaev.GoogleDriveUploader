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
        private DriveService GoogleApi;

        [Test]
        public async Task It_should_upload_Alphabet_folder()
        {
            // Given
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
            var gdrive = await GetGDriveTree(GoogleApi);
            Assert.That(gdrive["Alphabet"], Is.Not.Null);
            Assert.That((string) gdrive["Alphabet"]["a.txt"].content, Is.EqualTo(ToBase64String(files[0].content)));
            Assert.That((string) gdrive["Alphabet"]["B.txt"].content, Is.EqualTo(ToBase64String(files[1].content)));
            Assert.That((string) gdrive["Alphabet"]["Cc.txt"].content, Is.EqualTo(ToBase64String(files[2].content)));
        }

        [Test]
        public async Task It_should_upload_A_and_AA_and_B_folders()
        {
            // Given
            var src = GetTempDir();
            Directory.CreateDirectory(Path.Combine(src, "A"));
            Directory.CreateDirectory(Path.Combine(src, "A", "AA"));
            Directory.CreateDirectory(Path.Combine(src, "B"));
            var f1 = new {name = "1.txt", content = RandomString()};
            var f2 = new {name = "2.txt", content = RandomString()};
            var f3 = new {name = "3.txt", content = RandomString()};
            File.WriteAllText(Path.Combine(src, "A", f1.name), f1.content);
            File.WriteAllText(Path.Combine(src, "A", "AA", f2.name), f2.content);
            File.WriteAllText(Path.Combine(src, "B", f3.name), f3.content);
            var uploader = new Uploader(new DbDatabase(), Logger, Config.ReadFromAppSettings());

            // When
            await uploader.Copy(src, src, "GDriveTest");

            // Then
            var gdrive = await GetGDriveTree(GoogleApi);
            Assert.That((bool)gdrive["A"].dir, Is.True);
            Assert.That((bool)gdrive["A"]["AA"].dir, Is.True);
            Assert.That((bool)gdrive["B"].dir, Is.True);
            Assert.That((string) gdrive["A"]["1.txt"].content,
                Is.EqualTo(ToBase64String(f1.content)));
            Assert.That((string) gdrive["A"]["AA"]["2.txt"].content,
                Is.EqualTo(ToBase64String(f2.content)));
            Assert.That((string) gdrive["B"]["3.txt"].content,
                Is.EqualTo(ToBase64String(f3.content)));
        }

        [Test]
        public async Task It_should_upload_Alphabet_then_Notes_folders()
        {
        }

        [Test]
        public async Task It_should_upload_files_when_base_is_parent_of_source()
        {
        }

        [Test]
        public async Task It_should_warn_when_MD5_differs()
        {
        }
        
        [Test]
        public async Task It_should_upload_a_lot_of_files_in_folder()
        {
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

        [SetUp]
        public async Task PrepareTestFolders()
        {
            GoogleApi = await DriveServiceExt.Connect(Config.ReadFromAppSettings());
            var tempDir = new DirectoryInfo(GetTempDir());
            tempDir.Create();
            foreach (var file in tempDir.GetFiles())
                file.Delete();
            foreach (var dir in tempDir.GetDirectories())
                dir.Delete(true);
            var testDir = await GoogleApi.EnsureFolderCreated("root", "GDriveTest");
            foreach (var file in await GoogleApi.ListFoldersAndFiles(testDir.Id))
                await GoogleApi.Files.Delete(file.Id).ExecuteAsync();
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