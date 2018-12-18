using System.ComponentModel.DataAnnotations;

namespace Gaev.GoogleDriveUploader.Domain
{
    public class KeyValueStore
    {
        [Key] public string Key { get; set; }
        public string Value { get; set; }
    }
}