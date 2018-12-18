using System;
using System.ComponentModel.DataAnnotations;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class UploadingSession
    {
        [Key] public int Id { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public LocalFolder CurrentFolder { get; set; }
    }
}