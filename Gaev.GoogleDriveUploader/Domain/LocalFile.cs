using System;
using System.ComponentModel.DataAnnotations;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class LocalFile
    {
        [Key] public string Name { get; set; }
        public LocalFolder Folder { get; set; }
        public string Md5 { get; set; }
        public long Size { get; set; }
        public string GDriveId { get; set; }
        public DateTime SeenAt { get; set; }
        public DateTime? UploadedAt { get; set; }
    }
}