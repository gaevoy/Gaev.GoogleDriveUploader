using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class LocalFolder
    {
        [Key] public string Name { get; set; }
        public LocalFolder Parent { get; set; }
        public List<LocalFile> Files { get; set; }
        public string GDriveId { get; set; }
        public DateTime SeenAt { get; set; }
        public DateTime? UploadedAt { get; set; }
    }
}