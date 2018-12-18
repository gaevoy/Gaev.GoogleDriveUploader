using System.Linq;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gaev.GoogleDriveUploader.EntityFramework
{
    public class DbDatabase : IDatabase
    {
        public async Task<LocalFolder> GetFolder(string name)
        {
            using (var db = Open())
            {
                return await db.Folders
                    .Where(e => e.Name == name)
                    .Include(e => e.Files)
                    .FirstOrDefaultAsync();
            }
        }

        public async Task Update(LocalFolder folder)
        {
            using (var db = Open())
            {
                db.Folders.Attach(folder);
                db.Entry(folder).State = EntityState.Modified;
                await db.SaveChangesAsync();
            }
        }

        public async Task Insert(LocalFolder folder)
        {
            using (var db = Open())
            {
                db.Folders.Add(folder);
                await db.SaveChangesAsync();
            }
        }

        public async Task Update(LocalFile file)
        {
            using (var db = Open())
            {
                db.Files.Attach(file);
                db.Entry(file).State = EntityState.Modified;
                await db.SaveChangesAsync();
            }
        }

        public async Task Insert(LocalFile file)
        {
            using (var db = Open())
            {
                db.Files.Add(file);
                await db.SaveChangesAsync();
            }
        }

        private static DbSession Open(bool detectChanges = false)
        {
            var db = new DbSession();
            db.ChangeTracker.AutoDetectChangesEnabled = detectChanges;
            return db;
        }
    }
}