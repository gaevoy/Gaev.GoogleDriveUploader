using System.Threading.Tasks;

namespace Gaev.GoogleDriveUploader.Domain
{
    public interface IDatabase
    {
        Task<LocalFolder> GetFolder(string name);
        Task Insert(LocalFolder folder);
        Task Update(LocalFolder folder);
        Task Insert(LocalFile file);
        Task Update(LocalFile file);
    }
}