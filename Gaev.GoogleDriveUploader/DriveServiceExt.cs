using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

namespace Gaev.GoogleDriveUploader
{
    public static class DriveServiceExt
    {
        public static async Task<IList<File>> ListRootFolders(this DriveService cli)
        {
            var req = cli.Files.List();
            req.Q = "'root' in parents and mimeType='application/vnd.google-apps.folder' and trashed=false";
            return (await req.ExecuteAsync()).Files;
        }
    }
}