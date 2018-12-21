using Gaev.GoogleDriveUploader.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gaev.GoogleDriveUploader.EntityFramework
{
    public class DbSession : DbContext
    {
        public DbSet<LocalFolder> Folders { get; set; }
        public DbSet<LocalFile> Files { get; set; }
        public DbSet<UploadingSession> Sessions { get; set; }
        public DbSet<KeyValueStore> Store { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=GoogleDriveUploader.db");
        }
    }
}