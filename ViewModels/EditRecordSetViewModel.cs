using System.Collections.Generic;
using System.Linq;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class EditRecordSetViewModel : ViewModelBase
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public long Ttl { get; set; }
        public string Value { get; set; } // A single string for simplicity, for multiple values use newline

        public List<string> RecordTypes { get; } = ["A", "AAAA", "CNAME", "MX", "NS", "PTR", "SOA", "SPF", "SRV", "TXT"];
        public ResourceRecordSetModel OriginalRecord { get; }

        public EditRecordSetViewModel(ResourceRecordSetModel? recordToEdit = null)
        {
            if (recordToEdit != null)
            {
                // Editing an existing record
                OriginalRecord = recordToEdit;
                Name = recordToEdit.Name;
                Type = recordToEdit.Type;
                Ttl = recordToEdit.TTL;
                Value = string.Join("\n", recordToEdit.ResourceRecords);
            }
            else
            {
                // Creating a new record
                OriginalRecord = new ResourceRecordSetModel();
                Name = "";
                Type = "A";
                Ttl = 300;
                Value = "";
            }
        }
    }
}
