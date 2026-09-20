using System;

namespace AwsManager.Models
{
    public class S3ItemModel
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty; // Can be "Bucket", "Folder", "File", or "Navigation"
        public string? Size { get; set; }
        public DateTime? LastModified { get; set; }
    }
}
