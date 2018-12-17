namespace Gaev.GoogleDriveUploader
{
    public class Config
    {
        public string ClientId { get; set; } = "228215886134-2385amfq94kqbppmc65vov5js3festun.apps.googleusercontent.com";
        public string ClientSecret { get; set; } = "...";

        public static Config ReadFromAppSettings()
        {
            return new Config();
        }
    }
    
}