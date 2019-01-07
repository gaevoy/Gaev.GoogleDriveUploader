using System.Threading;

namespace Gaev.GoogleDriveUploader
{
    public class UploadingContext
    {
        public int NumberOfFailedFiles { get; set; }
        public int NumberOfFailedFolders { get; set; }
        public long SizeUploaded { get; set; }
        public bool RemainsOnly { get; set; }
        public bool EstimateOnly { get; set; }
        public CancellationToken Cancellation { get; set; }
    }
}